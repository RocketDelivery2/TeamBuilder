using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Tests.Application;
using Xunit.Abstractions;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// The private QA basketball flow exactly as the web client drives it, over HTTP on a real,
/// fully migrated SQL Server: one atomic create (event + participant x 10 + optional host
/// spot), the detail read model, my games with hosted games, the day-of-game lifecycle, a
/// no-show refill and a host transfer. Plus the races the new host-only writes introduce:
/// host assignment against self-claims on the last spots, and a stale former host's writes
/// against a transfer. Every outcome must be a clean 2xx, 403 or 409, never a 500.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class PrivateQaBasketballSqlServerIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _fixture;
    private readonly ITestOutputHelper _output;
    private SqlServerTestDatabase _db = null!;
    private SqlServerWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public PrivateQaBasketballSqlServerIntegrationTests(SqlServerContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "privateqa");
        await _db.MigrateToAsync();
        _factory = new SqlServerWebApplicationFactory(_db.ConnectionString);
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task WednesdayPickup_ThroughTheWebClientContract_FromCreateToRefillAndTransfer()
    {
        // 1-3. An organizer-only host creates Wednesday 8 PM, 10 players, in one request.
        var (hostId, hostToken) = await NewLinkedPlayerAsync();
        var start = NextWednesdayEightPmUtc();
        var created = await SendAsync(HttpMethod.Post, "/api/v1/events", hostToken, new
        {
            name = "Wednesday 8 PM pickup basketball",
            category = "basketball",
            eventDateUtc = start,
            scheduledEndUtc = start.AddHours(2),
            location = "Rec center court 2",
            rosterRequirements = new[] { new { roleCode = "participant", requiredCount = 10 } },
            hostParticipates = false
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var occurrenceId = (await created.Content.ReadFromJsonAsync<EventDto>())!.Id;

        // 4-5. The shared link opens the detail: 0/10, and the host sees it under my games.
        var detail = await DetailAsync(occurrenceId);
        (detail.RequiredCount, detail.SupplyCount, detail.IsRosterReady).Should().Be((10, 0, false));
        var requirementId = detail.Requirements.Single().Id;
        var hosted = await MyGamesAsync(hostToken, includeHosted: true);
        hosted.Items.Single().Should().Match<PlayerOccurrenceDto>(i => i.OccurrenceId == occurrenceId && i.IsHost && i.MyAssignmentId == null);
        (await MyGamesAsync(hostToken, includeHosted: false)).Items.Should().BeEmpty();

        // 6. Ten players join; one more is refused (RequirementFull, not retried).
        var players = new List<(Guid Id, string Token, Guid AssignmentId)>();
        for (var i = 0; i < 10; i++)
        {
            var (playerId, token) = await NewLinkedPlayerAsync();
            var claim = await SendAsync(HttpMethod.Post, ClaimsUrl(occurrenceId), token, new { requirementId });
            claim.StatusCode.Should().Be(HttpStatusCode.Created);
            players.Add((playerId, token, (await claim.Content.ReadFromJsonAsync<RosterAssignmentDto>())!.Id));
        }
        (await DetailAsync(occurrenceId)).IsRosterReady.Should().BeTrue();
        var (_, latecomerToken) = await NewLinkedPlayerAsync();
        var late = await SendAsync(HttpMethod.Post, ClaimsUrl(occurrenceId), latecomerToken, new { requirementId });
        (late.StatusCode, await CodeAsync(late)).Should().Be((HttpStatusCode.Conflict, "RequirementFull"));

        // A double tap is the idempotent 200, and the player sees the game in my games.
        (await SendAsync(HttpMethod.Post, ClaimsUrl(occurrenceId), players[0].Token, new { requirementId })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await MyGamesAsync(players[0].Token, includeHosted: true)).Items.Single().MyAssignmentStatus.Should().Be(RosterAssignmentStatus.Confirmed);

        // 7. One player leaves and rejoins (a new row; the old one is kept).
        (await SendAsync(HttpMethod.Post, ActionUrl(occurrenceId, players[9].AssignmentId, "leave"), players[9].Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await DetailAsync(occurrenceId)).SupplyCount.Should().Be(9);
        var rejoin = await SendAsync(HttpMethod.Post, ClaimsUrl(occurrenceId), players[9].Token, new { requirementId });
        rejoin.StatusCode.Should().Be(HttpStatusCode.Created);
        players[9] = (players[9].Id, players[9].Token, (await rejoin.Content.ReadFromJsonAsync<RosterAssignmentDto>())!.Id);

        // 8-9. Check in nine, start play, activate them.
        foreach (var player in players.Take(9))
            (await SendAsync(HttpMethod.Post, ActionUrl(occurrenceId, player.AssignmentId, "check-in"), hostToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Put, $"/api/v1/events/{occurrenceId}", hostToken, new { status = EventStatus.InProgress })).StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var player in players.Take(9))
            (await SendAsync(HttpMethod.Post, ActionUrl(occurrenceId, player.AssignmentId, "activate"), hostToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        // 10-11. The tenth never shows: the vacancy reopens at once.
        (await SendAsync(HttpMethod.Post, ActionUrl(occurrenceId, players[9].AssignmentId, "no-show"), hostToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        detail = await DetailAsync(occurrenceId);
        (detail.SupplyCount, detail.OpenQuantity, detail.IsRosterReady).Should().Be((9, 1, false));
        detail.Participants.Should().HaveCount(9).And.OnlyContain(p => p.Status == RosterAssignmentStatus.Active);

        // 12. The latecomer claims the reopened spot.
        var refill = await SendAsync(HttpMethod.Post, ClaimsUrl(occurrenceId), latecomerToken, new { requirementId });
        refill.StatusCode.Should().Be(HttpStatusCode.Created);
        (await DetailAsync(occurrenceId)).IsRosterReady.Should().BeTrue();

        // 13. Hosting moves to a player on court; the old host can no longer act.
        var newHost = players[0];
        (await SendAsync(HttpMethod.Post, $"/api/v1/events/{occurrenceId}/host/transfer", hostToken, new { newHostPlayerId = newHost.Id })).StatusCode.Should().Be(HttpStatusCode.OK);
        var stale = await SendAsync(HttpMethod.Post, RequirementsUrl(occurrenceId), hostToken, new { roleCode = "referee", requiredCount = 1 });
        stale.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, RequirementsUrl(occurrenceId), newHost.Token, new { roleCode = "referee", requiredCount = 1 })).StatusCode.Should().Be(HttpStatusCode.Created);
        var asNewHost = await DetailAsync(occurrenceId, newHost.Token);
        (asNewHost.IsHost, asNewHost.Participants.Single(p => p.PlayerId == newHost.Id).IsHost).Should().Be((true, true));
        (await MyGamesAsync(hostToken, includeHosted: true)).Items.Should().BeEmpty("the former host neither hosts nor plays any more");

        // 14. Every participation row is kept: 10 claims + 1 rejoin + 1 refill.
        await using var verify = _db.CreateContext();
        var rows = await verify.RosterAssignments.AsNoTracking().Where(a => a.OccurrenceId == occurrenceId).ToListAsync();
        rows.Should().HaveCount(12);
        rows.Count(a => a.Status == RosterAssignmentStatus.Cancelled && a.ExitReason == RosterExitReason.PlayerLeft).Should().Be(1);
        rows.Count(a => a.Status == RosterAssignmentStatus.NoShow).Should().Be(1);
        rows.Count(a => a.Status == RosterAssignmentStatus.Active).Should().Be(9);
        (await verify.TeamMembers.CountAsync()).Should().Be(0);
        _ = hostId;
    }

    [Fact]
    public async Task HostAssignment_RacingSelfClaims_ForTheLastSpots_NeverOverbooks()
    {
        var (_, hostToken) = await NewLinkedPlayerAsync();
        var (occurrenceId, requirementId) = await CreatePickupAsync(hostToken, hostParticipates: true);
        for (var i = 0; i < 7; i++)
            (await SendAsync(HttpMethod.Post, ClaimsUrl(occurrenceId), (await NewLinkedPlayerAsync()).Token, new { requirementId })).EnsureSuccessStatusCode();

        // 2 spots left: 3 host assignments and 20 self-claims at once.
        var operations = new List<Func<Task<HttpResponseMessage>>>();
        for (var i = 0; i < 3; i++)
        {
            var (assignee, _) = await NewLinkedPlayerAsync();
            operations.Add(() => SendAsync(HttpMethod.Post, AssignmentsUrl(occurrenceId), hostToken, new { playerId = assignee, requirementId }));
        }
        for (var i = 0; i < 20; i++)
        {
            var (_, token) = await NewLinkedPlayerAsync();
            operations.Add(() => SendAsync(HttpMethod.Post, ClaimsUrl(occurrenceId), token, new { requirementId }));
        }

        var responses = await RaceAsync(operations);

        var codes = await CodesAsync(responses);
        _output.WriteLine(string.Join(", ", responses.Select((r, i) => $"{(int)r.StatusCode}:{codes[i]}")));
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().BeLessThanOrEqualTo(2);
        codes.Where(c => c is not null).Should().OnlyContain(c => c == "RequirementFull" || c == "RosterChanged");
        (await LiveSupplyAsync(requirementId)).Should().BeLessThanOrEqualTo(10);
        (await LiveSupplyAsync(requirementId)).Should().Be(8 + responses.Count(r => r.StatusCode == HttpStatusCode.Created));
    }

    [Fact]
    public async Task StaleHostWrites_RacingATransfer_NeverWriteAfterLosingAuthority()
    {
        var (hostId, hostToken) = await NewLinkedPlayerAsync();
        var (newHostId, _) = await NewLinkedPlayerAsync();
        var (occurrenceId, requirementId) = await CreatePickupAsync(hostToken, hostParticipates: false);

        var operations = new List<Func<Task<HttpResponseMessage>>>
        {
            () => SendAsync(HttpMethod.Post, $"/api/v1/events/{occurrenceId}/host/transfer", hostToken, new { newHostPlayerId = newHostId })
        };
        for (var i = 0; i < 6; i++)
        {
            var (assignee, _) = await NewLinkedPlayerAsync();
            operations.Add(() => SendAsync(HttpMethod.Post, AssignmentsUrl(occurrenceId), hostToken, new { playerId = assignee, requirementId }));
        }
        for (var i = 0; i < 4; i++)
        {
            var role = $"role{i}";
            operations.Add(() => SendAsync(HttpMethod.Post, RequirementsUrl(occurrenceId), hostToken, new { roleCode = role, requiredCount = 1 }));
        }

        var responses = await RaceAsync(operations);

        var codes = await CodesAsync(responses);
        _output.WriteLine(string.Join(", ", responses.Select((r, i) => $"{(int)r.StatusCode}:{codes[i]}")));
        responses.Should().OnlyContain(r =>
            r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Created ||
            r.StatusCode == HttpStatusCode.Forbidden || r.StatusCode == HttpStatusCode.Conflict);

        await using var verify = _db.CreateContext();
        var occurrence = await verify.Events.AsNoTracking().SingleAsync(e => e.Id == occurrenceId);
        if (responses[0].StatusCode == HttpStatusCode.OK)
        {
            occurrence.HostId.Should().Be(newHostId);
            // Every write the old host got through committed before the transfer did, so each
            // is older than the occurrence's last update (the transfer itself).
            var assignments = await verify.RosterAssignments.AsNoTracking().Where(a => a.OccurrenceId == occurrenceId).ToListAsync();
            assignments.Should().OnlyContain(a => a.CreatedAtUtc <= occurrence.UpdatedAtUtc);
        }
        else
        {
            responses[0].StatusCode.Should().Be(HttpStatusCode.Conflict);
            occurrence.HostId.Should().Be(hostId);
        }
        // After the race, authority is exactly the stored host's.
        var after = await SendAsync(HttpMethod.Post, RequirementsUrl(occurrenceId), hostToken, new { roleCode = "after", requiredCount = 1 });
        after.StatusCode.Should().Be(occurrence.HostId == hostId ? HttpStatusCode.Created : HttpStatusCode.Forbidden);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string RosterUrl(Guid occurrenceId) => $"/api/v1/events/{occurrenceId}/roster";
    private static string ClaimsUrl(Guid occurrenceId) => $"{RosterUrl(occurrenceId)}/claims";
    private static string RequirementsUrl(Guid occurrenceId) => $"{RosterUrl(occurrenceId)}/requirements";
    private static string AssignmentsUrl(Guid occurrenceId) => $"{RosterUrl(occurrenceId)}/assignments";
    private static string ActionUrl(Guid occurrenceId, Guid assignmentId, string action) => $"{AssignmentsUrl(occurrenceId)}/{assignmentId}/{action}";

    private static DateTime NextWednesdayEightPmUtc()
    {
        var day = DateTime.UtcNow.Date.AddDays(1);
        while (day.DayOfWeek != DayOfWeek.Wednesday)
            day = day.AddDays(1);
        return DateTime.SpecifyKind(day.AddHours(20), DateTimeKind.Utc);
    }

    private async Task<(Guid OccurrenceId, Guid RequirementId)> CreatePickupAsync(string hostToken, bool hostParticipates)
    {
        var start = NextWednesdayEightPmUtc();
        var created = await SendAsync(HttpMethod.Post, "/api/v1/events", hostToken, new
        {
            name = "Pickup",
            eventDateUtc = start,
            rosterRequirements = new[] { new { roleCode = "participant", requiredCount = 10 } },
            hostParticipates
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var occurrenceId = (await created.Content.ReadFromJsonAsync<EventDto>())!.Id;
        return (occurrenceId, (await DetailAsync(occurrenceId)).Requirements.Single().Id);
    }

    private async Task<OccurrenceDetailDto> DetailAsync(Guid occurrenceId, string? token = null)
    {
        var response = token is null
            ? await _client.GetAsync($"/api/v1/events/{occurrenceId}/detail")
            : await SendAsync(HttpMethod.Get, $"/api/v1/events/{occurrenceId}/detail", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OccurrenceDetailDto>())!;
    }

    private async Task<PlayerOccurrencePageDto> MyGamesAsync(string token, bool includeHosted)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/v1/players/me/occurrences?includeHosted={includeHosted}", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PlayerOccurrencePageDto>())!;
    }

    private async Task<(Guid PlayerId, string Token)> NewLinkedPlayerAsync()
    {
        var playerId = Guid.NewGuid();
        return (playerId, await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, playerId));
    }

    private async Task<int> LiveSupplyAsync(Guid requirementId)
    {
        await using var context = _db.CreateContext();
        return await context.RosterAssignments.CountAsync(a =>
            a.RequirementId == requirementId && a.Status >= RosterAssignmentStatus.Reserved && a.Status <= RosterAssignmentStatus.Active);
    }

    /// <summary>Starts every operation, then releases them together through one gate.</summary>
    private static async Task<List<HttpResponseMessage>> RaceAsync(IEnumerable<Func<Task<HttpResponseMessage>>> operations)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = operations.Select(async operation =>
        {
            await gate.Task;
            return await operation();
        }).ToList();
        gate.SetResult();
        var responses = (await Task.WhenAll(tasks)).ToList();
        responses.Should().NotContain(r => r.StatusCode == HttpStatusCode.InternalServerError);
        return responses;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        using var document = JsonDocument.Parse(text);
        return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static async Task<List<string?>> CodesAsync(IEnumerable<HttpResponseMessage> responses)
    {
        var codes = new List<string?>();
        foreach (var response in responses)
            codes.Add(response.StatusCode == HttpStatusCode.Conflict ? await CodeAsync(response) ?? "<none>" : null);
        return codes;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }
}
