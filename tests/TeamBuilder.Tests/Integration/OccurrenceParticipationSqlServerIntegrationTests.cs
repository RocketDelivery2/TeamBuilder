using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Tests.Application;
using Xunit.Abstractions;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// The day-of-game participation lifecycle, host transfer and the player schedule over HTTP
/// against a real, fully migrated SQL Server database: the Wednesday 8 PM pickup basketball
/// dogfood flow end to end, and the races that matter (competing host transfers, transitions
/// against leave/remove, no-show against leave, refill overlapping a departure, a host checking
/// everyone in at once). Requests are released together by a shared gate (no sleeps); every
/// outcome must be a clean 2xx or 409, never a 500.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class OccurrenceParticipationSqlServerIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _fixture;
    private readonly ITestOutputHelper _output;
    private SqlServerTestDatabase _db = null!;
    private SqlServerWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public OccurrenceParticipationSqlServerIntegrationTests(SqlServerContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "lifecycle");
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

    // ── Wednesday 8 PM pickup basketball ─────────────────────────────────────

    [Fact]
    public async Task BasketballDogfood_WednesdayPickup_FullGameDayLifecycle()
    {
        // 1. The host creates a standalone occurrence through the public API (no team).
        var hostId = Guid.NewGuid();
        var hostToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, hostId);
        var wednesday8Pm = NextWednesdayEightPmUtc();
        var created = await SendAsync(HttpMethod.Post, "/api/v1/events", hostToken, new
        {
            name = "Wednesday 8 PM pickup basketball",
            eventDateUtc = wednesday8Pm,
            category = "basketball",
            location = "Rec center court 2",
            maxParticipants = 10
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var occurrence = (await created.Content.ReadFromJsonAsync<EventDto>())!;
        (occurrence.TeamId, occurrence.HostId).Should().Be(((Guid?)null, hostId));
        var occurrenceId = occurrence.Id;

        // 2. One participant requirement of 10. The host's role holds no spot.
        var requirementId = await CreateRequirementAsync(hostToken, occurrenceId, 10);
        await ExpectSummaryAsync(occurrenceId, 0, ready: false);

        // 3. The host plays, so they claim like everyone else.
        var hostSpot = await ClaimedAsync(hostToken, occurrenceId, requirementId);

        // 4-5. Nine more players with no team membership claim; the roster is READY.
        var players = new List<(Guid Id, string Token, Guid AssignmentId)>();
        for (var i = 0; i < 9; i++)
        {
            var (playerId, token) = await NewLinkedPlayerAsync();
            players.Add((playerId, token, (await ClaimedAsync(token, occurrenceId, requirementId)).Id));
        }
        await ExpectSummaryAsync(occurrenceId, 10, ready: true);

        // 6. Every participant sees the game on their own schedule.
        foreach (var (_, token, assignmentId) in players)
        {
            var item = (await MyOccurrencesAsync(token)).Items.Should().ContainSingle().Subject;
            (item.OccurrenceId, item.MyAssignmentId, item.MyAssignmentStatus, item.IsHost).Should().Be((occurrenceId, assignmentId, RosterAssignmentStatus.Confirmed, false));
            (item.ScheduledStartUtc, item.Location, item.Category, item.HostPlayerId).Should().Be((wednesday8Pm, "Rec center court 2", "basketball", hostId));
            (item.MyRoleCode, item.RequiredCount, item.SupplyCount, item.OpenQuantity, item.IsRosterReady).Should().Be(("participant", 10, 10, 0, true));
        }
        (await MyOccurrencesAsync(hostToken)).Items.Should().ContainSingle().Which.IsHost.Should().BeTrue();

        // 7. The host checks everyone in as they arrive.
        foreach (var assignmentId in players.Select(p => p.AssignmentId).Append(hostSpot.Id))
            (await ActAsync(hostToken, occurrenceId, assignmentId, "check-in")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await MyOccurrencesAsync(players[0].Token)).Items.Single().MyAssignmentStatus.Should().Be(RosterAssignmentStatus.CheckedIn);

        // 8. Tip-off: the game is in progress and the host activates everyone.
        (await SendAsync(HttpMethod.Put, $"/api/v1/events/{occurrenceId}", hostToken, new { status = EventStatus.InProgress })).StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var assignmentId in players.Select(p => p.AssignmentId).Append(hostSpot.Id))
            (await ActAsync(hostToken, occurrenceId, assignmentId, "activate")).StatusCode.Should().Be(HttpStatusCode.OK);
        await ExpectSummaryAsync(occurrenceId, 10, ready: true);

        // 9-10. One active player leaves early: 9/10, not ready.
        var leaver = players[4];
        var left = await LeaveAsync(leaver.Token, occurrenceId, leaver.AssignmentId);
        left.StatusCode.Should().Be(HttpStatusCode.OK);
        var departed = (await left.Content.ReadFromJsonAsync<RosterAssignmentDto>())!;
        (departed.Status, departed.ExitReason).Should().Be((RosterAssignmentStatus.Departed, RosterExitReason.PlayerLeft));
        await ExpectSummaryAsync(occurrenceId, 9, ready: false);
        (await MyOccurrencesAsync(leaver.Token)).Items.Should().BeEmpty();

        // 11-12. A replacement claims (with lineage): 10/10 READY again.
        var (replacementId, replacementToken) = await NewLinkedPlayerAsync();
        var claim = await ClaimAsync(replacementToken, occurrenceId, new { requirementId, replacesAssignmentId = leaver.AssignmentId });
        claim.StatusCode.Should().Be(HttpStatusCode.Created, await claim.Content.ReadAsStringAsync());
        var replacement = (await claim.Content.ReadFromJsonAsync<RosterAssignmentDto>())!;
        var summary = await ExpectSummaryAsync(occurrenceId, 10, ready: true);

        // 13-14. The original row is history; the replacement is a separate row.
        summary.Assignments.Should().HaveCount(11);
        var original = summary.Assignments.Single(a => a.Id == leaver.AssignmentId);
        (original.PlayerId, original.Status, original.CheckedInAtUtc.HasValue, original.ActivatedAtUtc.HasValue, original.DepartedAtUtc.HasValue)
            .Should().Be((leaver.Id, RosterAssignmentStatus.Departed, true, true, true));
        (replacement.Id, replacement.PlayerId, replacement.ReplacedAssignmentId, replacement.Status)
            .Should().NotBe((original.Id, original.PlayerId, (Guid?)null, original.Status));
        replacement.ReplacedAssignmentId.Should().Be(original.Id);
        (replacement.PlayerId, replacement.Status).Should().Be((replacementId, RosterAssignmentStatus.Confirmed));

        // 15. The host hands stewardship to a linked player who is on the court.
        var newHost = players[0];
        var transfer = await SendAsync(HttpMethod.Post, $"/api/v1/events/{occurrenceId}/host/transfer", hostToken, new { newHostPlayerId = newHost.Id });
        transfer.StatusCode.Should().Be(HttpStatusCode.OK, await transfer.Content.ReadAsStringAsync());
        (await ExpectSummaryAsync(occurrenceId, 10, ready: true)).Assignments.Should().HaveCount(11);

        // 16. The new host manages the lifecycle: checks in and activates the replacement.
        (await ActAsync(newHost.Token, occurrenceId, replacement.Id, "check-in")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ActAsync(newHost.Token, occurrenceId, replacement.Id, "activate")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await MyOccurrencesAsync(newHost.Token)).Items.Single().IsHost.Should().BeTrue();

        // 17. The old host can no longer use host-only endpoints, but still plays.
        (await ActAsync(hostToken, occurrenceId, players[1].AssignmentId, "no-show")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, $"{RosterUrl(occurrenceId)}/assignments/{players[1].AssignmentId}/remove", hostToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var oldHostView = (await MyOccurrencesAsync(hostToken)).Items.Single();
        (oldHostView.IsHost, oldHostView.MyAssignmentStatus).Should().Be((false, RosterAssignmentStatus.Active));

        // No Team entity anywhere in the flow.
        await using var verify = _db.CreateContext();
        (await verify.Teams.AnyAsync()).Should().BeFalse();
        (await verify.TeamMembers.AnyAsync()).Should().BeFalse();
        var final = await ExpectSummaryAsync(occurrenceId, 10, ready: true);
        final.Assignments.Count(a => a.Status == RosterAssignmentStatus.Active).Should().Be(10);
    }

    // ── host transfer races ──────────────────────────────────────────────────

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public async Task ConcurrentHostTransfers_ExactlyOneWins_TheRestAreClean409s(int contenders)
    {
        var (hostToken, occurrenceId, _) = await SeedPickupAsync();
        var targets = new List<Guid>();
        for (var i = 0; i < contenders; i++)
            targets.Add((await NewLinkedPlayerAsync()).PlayerId);

        var responses = await RaceAsync(targets.Select(target => (Func<Task<HttpResponseMessage>>)(() =>
            SendAsync(HttpMethod.Post, $"/api/v1/events/{occurrenceId}/host/transfer", hostToken, new { newHostPlayerId = target }))));

        var winners = responses.Where(r => r.StatusCode == HttpStatusCode.OK).ToList();
        winners.Should().ContainSingle();
        responses.Where(r => r.StatusCode != HttpStatusCode.OK).Should().OnlyContain(r =>
            r.StatusCode == HttpStatusCode.Conflict || r.StatusCode == HttpStatusCode.Forbidden);
        (await CodesAsync(responses)).Where(c => c is not null).Should().OnlyContain(c => c == RosterConflictCodes.OccurrenceChanged);

        var winner = (await winners[0].Content.ReadFromJsonAsync<EventDto>())!.HostId;
        await using var verify = _db.CreateContext();
        (await verify.Events.AsNoTracking().SingleAsync(e => e.Id == occurrenceId)).HostId.Should().Be(winner);
        _output.WriteLine($"{contenders} concurrent transfers: 1 x 200, " +
            $"{responses.Count(r => r.StatusCode == HttpStatusCode.Conflict)} x 409 OccurrenceChanged, " +
            $"{responses.Count(r => r.StatusCode == HttpStatusCode.Forbidden)} x 403.");
    }

    [Fact]
    public async Task HostTransfer_RacingTheOldHostsCheckIns_NeverLetsAFormerHostWrite()
    {
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync();
        var holders = await FillAsync(occurrenceId, requirementId, 8);
        var (newHostId, _) = await NewLinkedPlayerAsync();

        var operations = new List<Func<Task<HttpResponseMessage>>>
        {
            () => SendAsync(HttpMethod.Post, $"/api/v1/events/{occurrenceId}/host/transfer", hostToken, new { newHostPlayerId = newHostId })
        };
        operations.AddRange(holders.Select(h => (Func<Task<HttpResponseMessage>>)(() => ActAsync(hostToken, occurrenceId, h.AssignmentId, "check-in"))));
        var responses = await RaceAsync(operations);

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Forbidden || r.StatusCode == HttpStatusCode.Conflict);
        await using var verify = _db.CreateContext();
        var stored = await verify.Events.AsNoTracking().SingleAsync(e => e.Id == occurrenceId);
        var rows = await verify.RosterAssignments.AsNoTracking().Where(a => a.OccurrenceId == occurrenceId).ToListAsync();
        rows.Should().HaveCount(8).And.OnlyContain(a => a.Status == RosterAssignmentStatus.Confirmed || a.Status == RosterAssignmentStatus.CheckedIn);

        if (responses[0].StatusCode == HttpStatusCode.OK)
        {
            // Every check-in that committed did so before the transfer: its UPDATE carried the
            // occurrence RowVersion the transfer then had to supersede.
            stored.HostId.Should().Be(newHostId);
            var transferredAt = stored.UpdatedAtUtc!.Value;
            rows.Where(a => a.Status == RosterAssignmentStatus.CheckedIn).Should().OnlyContain(a => a.CheckedInAtUtc <= transferredAt);
        }
        _output.WriteLine($"transfer: {(int)responses[0].StatusCode}; check-ins: " +
            string.Join(", ", responses.Skip(1).GroupBy(r => (int)r.StatusCode).Select(g => $"{g.Count()} x {g.Key}")));
    }

    // ── lifecycle races ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("check-in")]
    [InlineData("no-show")]
    public async Task Transition_RacingTheHoldersLeave_EndsInExactlyOneCleanOutcome(string action)
    {
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync();
        var holder = (await FillAsync(occurrenceId, requirementId, 1))[0];

        var responses = await RaceAsync(
        [
            () => ActAsync(hostToken, occurrenceId, holder.AssignmentId, action),
            () => LeaveAsync(holder.Token, occurrenceId, holder.AssignmentId)
        ]);

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
        var codes = (await CodesAsync(responses)).Where(c => c is not null).ToList();
        codes.Should().OnlyContain(c => c == RosterConflictCodes.AssignmentEnded || c == RosterConflictCodes.RosterChanged);
        var row = await FindAsync(holder.AssignmentId);
        row.Status.Should().BeOneOf(RosterAssignmentStatus.CheckedIn, RosterAssignmentStatus.NoShow, RosterAssignmentStatus.Cancelled, RosterAssignmentStatus.Departed);
        (await CountAsync(a => a.OccurrenceId == occurrenceId)).Should().Be(1);
        _output.WriteLine($"{action} vs leave: {string.Join(", ", responses.Select(r => (int)r.StatusCode))} → {row.Status}/{row.ExitReason}.");
    }

    [Fact]
    public async Task Transition_RacingTheHostsRemove_OfTheSameAssignment_OneWins()
    {
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync();
        var holder = (await FillAsync(occurrenceId, requirementId, 1))[0];

        var responses = await RaceAsync(
        [
            () => ActAsync(hostToken, occurrenceId, holder.AssignmentId, "check-in"),
            () => SendAsync(HttpMethod.Post, $"{RosterUrl(occurrenceId)}/assignments/{holder.AssignmentId}/remove", hostToken)
        ]);

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
        var row = await FindAsync(holder.AssignmentId);
        if (responses[1].StatusCode == HttpStatusCode.OK)
            row.ExitReason.Should().Be(RosterExitReason.HostRemoved);
        else
            row.Status.Should().Be(RosterAssignmentStatus.CheckedIn);
    }

    [Fact]
    public async Task NoShowAndLeave_Simultaneously_ReleaseTheSpotExactlyOnce()
    {
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync(requiredCount: 10);
        var holders = await FillAsync(occurrenceId, requirementId, 10);
        var target = holders[0];

        // Two no-shows and a leave of the same assignment, plus a leave of another one.
        var responses = await RaceAsync(
        [
            () => ActAsync(hostToken, occurrenceId, target.AssignmentId, "no-show"),
            () => ActAsync(hostToken, occurrenceId, target.AssignmentId, "no-show"),
            () => LeaveAsync(target.Token, occurrenceId, target.AssignmentId),
            () => LeaveAsync(holders[1].Token, occurrenceId, holders[1].AssignmentId)
        ]);

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
        responses.Take(3).Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses[3].StatusCode.Should().Be(HttpStatusCode.OK);
        (await LiveSupplyAsync(requirementId)).Should().Be(8);
        var summary = await ExpectSummaryAsync(occurrenceId, 8, ready: false);
        summary.OpenQuantity.Should().Be(2);
        (await CountAsync(a => a.OccurrenceId == occurrenceId)).Should().Be(10);
    }

    [Fact]
    public async Task ManyNoShows_AtOnce_ReleaseExactlyThatManySpots()
    {
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync(requiredCount: 10);
        var holders = await FillAsync(occurrenceId, requirementId, 10);

        // The host marks five no-shows at once; each also races a duplicate tap.
        var operations = holders.Take(5)
            .SelectMany(h => new[] { h, h })
            .Select(h => (Func<Task<HttpResponseMessage>>)(() => ActAsync(hostToken, occurrenceId, h.AssignmentId, "no-show")));
        var responses = await RaceAsync(operations);

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().BeLessThanOrEqualTo(5);
        // Retry any retryable loss; duplicates stay AssignmentEnded.
        foreach (var holder in holders.Take(5))
        {
            if ((await FindAsync(holder.AssignmentId)).Status == RosterAssignmentStatus.NoShow)
                continue;
            (await ActAsync(hostToken, occurrenceId, holder.AssignmentId, "no-show")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await LiveSupplyAsync(requirementId)).Should().Be(5);
        (await CountAsync(a => a.OccurrenceId == occurrenceId && a.Status == RosterAssignmentStatus.NoShow && a.ExitReason == RosterExitReason.NoShow)).Should().Be(5);
        (await CountAsync(a => a.OccurrenceId == occurrenceId)).Should().Be(10);
    }

    [Fact]
    public async Task ReplacementClaims_OverlappingANoShow_NeverOverbook()
    {
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync(requiredCount: 10);
        var holders = await FillAsync(occurrenceId, requirementId, 10);
        var claimants = new List<string>();
        for (var i = 0; i < 10; i++)
            claimants.Add((await NewLinkedPlayerAsync()).Token);

        var operations = new List<Func<Task<HttpResponseMessage>>>
        {
            () => ActAsync(hostToken, occurrenceId, holders[0].AssignmentId, "no-show")
        };
        operations.AddRange(claimants.Select(token => (Func<Task<HttpResponseMessage>>)(() =>
            ClaimAsync(token, occurrenceId, new { requirementId }))));
        var responses = await RaceAsync(operations);

        responses[0].StatusCode.Should().Be(HttpStatusCode.OK);
        var winners = responses.Skip(1).Count(r => r.StatusCode == HttpStatusCode.Created);
        winners.Should().BeLessThanOrEqualTo(1);
        responses.Skip(1).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.Conflict);
        (await LiveSupplyAsync(requirementId)).Should().Be(9 + winners);
        if (winners == 0)
            (await ClaimAsync(claimants[0], occurrenceId, new { requirementId })).StatusCode.Should().Be(HttpStatusCode.Created);

        (await LiveSupplyAsync(requirementId)).Should().Be(10);
        (await FindAsync(holders[0].AssignmentId)).Status.Should().Be(RosterAssignmentStatus.NoShow);
        (await CountAsync(a => a.OccurrenceId == occurrenceId)).Should().Be(11);
    }

    [Fact]
    public async Task HostChecksEveryoneInAtOnce_NoServerErrors_AndRetriesFinishTheJob()
    {
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync(requiredCount: 10);
        var holders = await FillAsync(occurrenceId, requirementId, 10);

        var responses = await RaceAsync(holders.Select(h => (Func<Task<HttpResponseMessage>>)(() =>
            ActAsync(hostToken, occurrenceId, h.AssignmentId, "check-in"))));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
        (await CodesAsync(responses)).Where(c => c is not null).Should().OnlyContain(c => c == RosterConflictCodes.RosterChanged);
        var firstRound = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        for (var i = 0; i < holders.Count; i++)
        {
            if (responses[i].StatusCode != HttpStatusCode.OK)
                (await ActAsync(hostToken, occurrenceId, holders[i].AssignmentId, "check-in")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await CountAsync(a => a.OccurrenceId == occurrenceId && a.Status == RosterAssignmentStatus.CheckedIn)).Should().Be(10);
        await ExpectSummaryAsync(occurrenceId, 10, ready: true);
        _output.WriteLine($"10 simultaneous check-ins by one host: {firstRound} x 200 in the first round, {10 - firstRound} x 409 RosterChanged.");
    }

    [Fact]
    public async Task EveryDocumentedFlow_KeepsAllHistoricalRows_AndDeleteIsRefused()
    {
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync(requiredCount: 4);
        var holders = await FillAsync(occurrenceId, requirementId, 4);
        (await LeaveAsync(holders[0].Token, occurrenceId, holders[0].AssignmentId)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, $"{RosterUrl(occurrenceId)}/assignments/{holders[1].AssignmentId}/remove", hostToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ActAsync(hostToken, occurrenceId, holders[2].AssignmentId, "no-show")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ActAsync(hostToken, occurrenceId, holders[3].AssignmentId, "check-in")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ActAsync(hostToken, occurrenceId, holders[3].AssignmentId, "activate")).StatusCode.Should().Be(HttpStatusCode.OK);
        await FillAsync(occurrenceId, requirementId, 3);

        var delete = await SendAsync(HttpMethod.Delete, $"/api/v1/events/{occurrenceId}", hostToken);

        delete.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodesAsync([delete])).Should().Equal(RosterConflictCodes.OccurrenceHasParticipationHistory);
        await using var verify = _db.CreateContext();
        var rows = await verify.RosterAssignments.AsNoTracking().Where(a => a.OccurrenceId == occurrenceId).ToListAsync();
        rows.Should().HaveCount(7);
        rows.Select(a => (a.Status, a.ExitReason)).Should().Contain(
        [
            (RosterAssignmentStatus.Cancelled, RosterExitReason.PlayerLeft),
            (RosterAssignmentStatus.Cancelled, RosterExitReason.HostRemoved),
            (RosterAssignmentStatus.NoShow, RosterExitReason.NoShow),
            (RosterAssignmentStatus.Active, null)
        ]);
        await ExpectSummaryAsync(occurrenceId, 4, ready: true, required: 4);
    }

    [Fact]
    public async Task MyOccurrences_OnSqlServer_PagesDeterministicallyWithTiedStarts()
    {
        var (meId, meToken) = await NewLinkedPlayerAsync();
        var hostId = Guid.NewGuid();
        var hostToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, hostId);
        var start = DateTime.UtcNow.AddDays(5);
        var ids = new List<Guid>();
        for (var i = 0; i < 7; i++)
        {
            var id = await SeedEventAsync(hostId, i < 4 ? start : start.AddHours(i));
            await ClaimedAsync(meToken, id, await CreateRequirementAsync(hostToken, id, 10));
            ids.Add(id);
        }

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await MyOccurrencesAsync(meToken, cursor is null ? "pageSize=3" : $"pageSize=3&cursor={cursor}");
            seen.AddRange(page.Items.Select(i => i.OccurrenceId));
            cursor = page.NextCursor;
        }
        while (cursor is not null && seen.Count < 20);

        seen.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(ids);
        (await MyOccurrencesAsync(meToken, "pageSize=100")).Items.Select(i => i.OccurrenceId).Should().Equal(seen);
        _ = meId;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string RosterUrl(Guid occurrenceId) => $"/api/v1/events/{occurrenceId}/roster";

    private static DateTime NextWednesdayEightPmUtc()
    {
        var day = DateTime.UtcNow.Date.AddDays(1);
        while (day.DayOfWeek != DayOfWeek.Wednesday)
            day = day.AddDays(1);
        return DateTime.SpecifyKind(day.AddHours(20), DateTimeKind.Utc);
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

    private static async Task<List<string?>> CodesAsync(IEnumerable<HttpResponseMessage> responses)
    {
        var codes = new List<string?>();
        foreach (var response in responses)
        {
            if (response.StatusCode != HttpStatusCode.Conflict)
            {
                codes.Add(null);
                continue;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            codes.Add(document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : "<none>");
        }
        return codes;
    }

    private async Task<(Guid PlayerId, string Token)> NewLinkedPlayerAsync()
    {
        var playerId = Guid.NewGuid();
        return (playerId, await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, playerId));
    }

    private async Task<(string HostToken, Guid OccurrenceId, Guid RequirementId)> SeedPickupAsync(int requiredCount = 10)
    {
        var hostId = Guid.NewGuid();
        var hostToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, hostId);
        var occurrenceId = await SeedEventAsync(hostId, NextWednesdayEightPmUtc());
        return (hostToken, occurrenceId, await CreateRequirementAsync(hostToken, occurrenceId, requiredCount));
    }

    private async Task<Guid> SeedEventAsync(Guid hostId, DateTime startUtc)
    {
        await using var context = _db.CreateContext();
        var occurrence = new EventOccurrence
        {
            Id = Guid.NewGuid(),
            Name = "Wednesday 8 PM pickup basketball",
            ScheduledStartUtc = startUtc,
            Status = EventStatus.Open,
            MaxParticipants = 10,
            HostId = hostId
        };
        context.Events.Add(occurrence);
        await context.SaveChangesAsync();
        return occurrence.Id;
    }

    private async Task<Guid> CreateRequirementAsync(string hostToken, Guid occurrenceId, int requiredCount)
    {
        var response = await SendAsync(HttpMethod.Post, $"{RosterUrl(occurrenceId)}/requirements", hostToken, new { roleCode = "participant", requiredCount });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RosterRequirementDto>())!.Id;
    }

    /// <summary>Self-claims <paramref name="count"/> spots with new linked players, sequentially.</summary>
    private async Task<List<(string Token, Guid AssignmentId)>> FillAsync(Guid occurrenceId, Guid requirementId, int count)
    {
        var holders = new List<(string, Guid)>();
        for (var i = 0; i < count; i++)
        {
            var (_, token) = await NewLinkedPlayerAsync();
            holders.Add((token, (await ClaimedAsync(token, occurrenceId, requirementId)).Id));
        }
        return holders;
    }

    private async Task<RosterAssignmentDto> ClaimedAsync(string token, Guid occurrenceId, Guid requirementId)
    {
        var response = await ClaimAsync(token, occurrenceId, new { requirementId });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RosterAssignmentDto>())!;
    }

    private Task<HttpResponseMessage> ClaimAsync(string token, Guid occurrenceId, object body) =>
        SendAsync(HttpMethod.Post, $"{RosterUrl(occurrenceId)}/claims", token, body);

    private Task<HttpResponseMessage> LeaveAsync(string token, Guid occurrenceId, Guid assignmentId) =>
        SendAsync(HttpMethod.Post, $"{RosterUrl(occurrenceId)}/assignments/{assignmentId}/leave", token);

    private Task<HttpResponseMessage> ActAsync(string token, Guid occurrenceId, Guid assignmentId, string action) =>
        SendAsync(HttpMethod.Post, $"{RosterUrl(occurrenceId)}/assignments/{assignmentId}/{action}", token);

    private async Task<PlayerOccurrencePageDto> MyOccurrencesAsync(string token, string? query = null)
    {
        var url = query is null ? "/api/v1/players/me/occurrences" : $"/api/v1/players/me/occurrences?{query}";
        var response = await SendAsync(HttpMethod.Get, url, token);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PlayerOccurrencePageDto>())!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private async Task<RosterSummaryDto> ExpectSummaryAsync(Guid occurrenceId, int supply, bool ready, int required = 10)
    {
        var summary = (await _client.GetFromJsonAsync<RosterSummaryDto>(RosterUrl(occurrenceId)))!;
        (summary.RequiredCount, summary.SupplyCount, summary.IsRosterReady).Should().Be((required, supply, ready));
        return summary;
    }

    private async Task<int> LiveSupplyAsync(Guid requirementId)
    {
        await using var context = _db.CreateContext();
        return await context.RosterAssignments.CountAsync(a =>
            a.RequirementId == requirementId && a.Status >= RosterAssignmentStatus.Reserved && a.Status <= RosterAssignmentStatus.Active);
    }

    private async Task<int> CountAsync(System.Linq.Expressions.Expression<Func<RosterAssignment, bool>> predicate)
    {
        await using var context = _db.CreateContext();
        return await context.RosterAssignments.CountAsync(predicate);
    }

    private async Task<RosterAssignment> FindAsync(Guid assignmentId)
    {
        await using var context = _db.CreateContext();
        return await context.RosterAssignments.AsNoTracking().SingleAsync(a => a.Id == assignmentId);
    }
}
