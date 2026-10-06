using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Tests.Application;
using Xunit.Abstractions;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// Player self-claim, self-leave and host removal over HTTP against a real, fully migrated SQL
/// Server database: final-spot races between many claimants, duplicate claims by one player,
/// claim/leave overlap, host assignment vs self-claim, in-progress refill and the 5-on-5
/// pickup basketball dogfood loop. Concurrent requests are released together by a shared gate
/// (no sleeps); every outcome must be a clean 2xx or 409, never a 500.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class RosterSelfClaimSqlServerIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _fixture;
    private readonly ITestOutputHelper _output;
    private SqlServerTestDatabase _db = null!;
    private SqlServerWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public RosterSelfClaimSqlServerIntegrationTests(SqlServerContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "selfclaim");
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

    // ── final-spot races ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(2)]
    [InlineData(20)]
    [InlineData(100)]
    [InlineData(200)]
    public async Task FinalSpot_ManyClaimants_ExactlyOneWins_AndNoneOverbooks(int claimants)
    {
        var (_, occurrenceId, requirementId) = await SeedPickupAsync(requiredCount: 10);
        await FillAsync(occurrenceId, requirementId, 9);
        var tokens = await NewPlayerTokensAsync(claimants);

        var stopwatch = Stopwatch.StartNew();
        var responses = await RaceAsync(tokens.Select(token => (Func<Task<HttpResponseMessage>>)(() =>
            ClaimAsync(token, occurrenceId, new { requirementId }))));
        stopwatch.Stop();

        var codes = await CodesAsync(responses);
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Where(r => r.StatusCode != HttpStatusCode.Created)
            .Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Conflict);
        codes.Where(c => c is not null).Should().OnlyContain(c => c == RosterConflictCodes.RequirementFull);

        var summary = await SummaryAsync(occurrenceId);
        (summary.SupplyCount, summary.OpenQuantity, summary.IsRosterReady).Should().Be((10, 0, true));
        (await LiveSupplyAsync(requirementId)).Should().Be(10);
        (await CountAsync(a => a.OccurrenceId == occurrenceId)).Should().Be(10);

        _output.WriteLine($"{claimants} claimants for 1 remaining spot: 1 x 201, {claimants - 1} x 409 RequirementFull, {stopwatch.ElapsedMilliseconds} ms wall clock for the race.");
    }

    [Fact]
    public async Task ManyClaimants_WithCapacityLeft_NeverOverbook_AndLosersMayRetry()
    {
        // 20 claimants for 5 open units: every concurrency loss while capacity remains is the
        // retryable RosterChanged; once full every loser sees RequirementFull.
        var (_, occurrenceId, requirementId) = await SeedPickupAsync(requiredCount: 10);
        await FillAsync(occurrenceId, requirementId, 5);
        var tokens = await NewPlayerTokensAsync(20);

        var responses = await RaceAsync(tokens.Select(token => (Func<Task<HttpResponseMessage>>)(() =>
            ClaimAsync(token, occurrenceId, new { requirementId }))));
        var codes = await CodesAsync(responses);

        var winners = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        winners.Should().BeInRange(1, 5);
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.Conflict);
        codes.Where(c => c is not null).Should().OnlyContain(c => c == RosterConflictCodes.RequirementFull || c == RosterConflictCodes.RosterChanged);
        (await LiveSupplyAsync(requirementId)).Should().Be(5 + winners);
        _output.WriteLine($"20 claimants for 5 open units, first round: {winners} x 201, " +
            $"{codes.Count(c => c == RosterConflictCodes.RosterChanged)} x RosterChanged, {codes.Count(c => c == RosterConflictCodes.RequirementFull)} x RequirementFull.");

        // Sequential retries by the losers fill exactly the remaining capacity.
        for (var i = 0; i < tokens.Count; i++)
        {
            if (responses[i].StatusCode == HttpStatusCode.Created)
                continue;
            var retry = await ClaimAsync(tokens[i], occurrenceId, new { requirementId });
            retry.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.Conflict);
        }

        (await LiveSupplyAsync(requirementId)).Should().Be(10);
        (await SummaryAsync(occurrenceId)).IsRosterReady.Should().BeTrue();
    }

    // ── duplicate claims by one player ───────────────────────────────────────

    [Fact]
    public async Task SamePlayer_ParallelClaims_CreateOneAssignment_AndEveryResponseReturnsIt()
    {
        var (_, occurrenceId, requirementId) = await SeedPickupAsync(requiredCount: 10);
        var token = (await NewPlayerTokensAsync(1))[0];

        var responses = await RaceAsync(Enumerable.Range(0, 10).Select(_ => (Func<Task<HttpResponseMessage>>)(() =>
            ClaimAsync(token, occurrenceId, new { requirementId }))));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.OK);
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        var ids = new List<Guid>();
        foreach (var response in responses)
            ids.Add((await response.Content.ReadFromJsonAsync<RosterAssignmentDto>())!.Id);
        ids.Distinct().Should().ContainSingle();
        (await CountAsync(a => a.OccurrenceId == occurrenceId)).Should().Be(1);
    }

    [Fact]
    public async Task SamePlayer_ParallelClaimsForTheLastSpot_StayIdempotent()
    {
        var (_, occurrenceId, requirementId) = await SeedPickupAsync(requiredCount: 10);
        await FillAsync(occurrenceId, requirementId, 9);
        var token = (await NewPlayerTokensAsync(1))[0];

        var responses = await RaceAsync(Enumerable.Range(0, 10).Select(_ => (Func<Task<HttpResponseMessage>>)(() =>
            ClaimAsync(token, occurrenceId, new { requirementId }))));

        // The copy that lost to its own twin sees the requirement full, then finds the claim it
        // already holds: still a 200 with that assignment, never RequirementFull.
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.OK);
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        (await LiveSupplyAsync(requirementId)).Should().Be(10);
    }

    [Fact]
    public async Task SamePlayer_RacingForTwoRequirements_HoldsOnlyOne()
    {
        var (hostToken, occurrenceId, guardId) = await SeedPickupAsync(requiredCount: 2, roleCode: "guard");
        var forwardId = await CreateRequirementAsync(hostToken, occurrenceId, "forward", 2);
        var token = (await NewPlayerTokensAsync(1))[0];

        var responses = await RaceAsync(
        [
            () => ClaimAsync(token, occurrenceId, new { requirementId = guardId }),
            () => ClaimAsync(token, occurrenceId, new { requirementId = forwardId })
        ]);

        responses.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.Created, HttpStatusCode.Conflict]);
        (await CodesAsync(responses)).Should().Contain(RosterConflictCodes.AlreadyParticipating);
        (await CountAsync(a => a.OccurrenceId == occurrenceId)).Should().Be(1);
    }

    // ── claim vs leave, host vs player ───────────────────────────────────────

    [Fact]
    public async Task ClaimAndLeave_Overlapping_NeverOverbook_AndKeepHistory()
    {
        var (_, occurrenceId, requirementId) = await SeedPickupAsync(requiredCount: 10);
        var holders = await FillAsync(occurrenceId, requirementId, 10);
        var leaver = holders[0];
        var claimants = await NewPlayerTokensAsync(5);

        var operations = new List<Func<Task<HttpResponseMessage>>>
        {
            () => LeaveAsync(leaver.Token, occurrenceId, leaver.AssignmentId)
        };
        operations.AddRange(claimants.Select(token => (Func<Task<HttpResponseMessage>>)(() =>
            ClaimAsync(token, occurrenceId, new { requirementId }))));

        var responses = await RaceAsync(operations);

        responses[0].StatusCode.Should().Be(HttpStatusCode.OK);
        var claims = responses.Skip(1).ToList();
        claims.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.Conflict);
        var winners = claims.Count(r => r.StatusCode == HttpStatusCode.Created);
        winners.Should().BeLessThanOrEqualTo(1);
        (await LiveSupplyAsync(requirementId)).Should().Be(9 + winners);

        // If every claim was turned away before the leave committed, the spot is open now.
        if (winners == 0)
        {
            (await ClaimAsync(claimants[0], occurrenceId, new { requirementId })).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        (await LiveSupplyAsync(requirementId)).Should().Be(10);
        var departed = await FindAsync(leaver.AssignmentId);
        (departed.Status, departed.ExitReason).Should().Be((RosterAssignmentStatus.Cancelled, RosterExitReason.PlayerLeft));
    }

    [Fact]
    public async Task HostAssignment_And_SelfClaim_RaceForTheFinalSpot_ExactlyOneWins()
    {
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync(requiredCount: 10);
        await FillAsync(occurrenceId, requirementId, 9);
        var assignedPlayerId = Guid.NewGuid();
        await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, assignedPlayerId);
        var claimantToken = (await NewPlayerTokensAsync(1))[0];

        var responses = await RaceAsync(
        [
            () => SendAsync(HttpMethod.Post, $"{RosterUrl(occurrenceId)}/assignments", hostToken, new { playerId = assignedPlayerId, requirementId }),
            () => ClaimAsync(claimantToken, occurrenceId, new { requirementId })
        ]);

        responses.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.Created, HttpStatusCode.Conflict]);
        (await CodesAsync(responses)).Where(c => c is not null).Should().Equal(RosterConflictCodes.RequirementFull);
        (await LiveSupplyAsync(requirementId)).Should().Be(10);
    }

    [Fact]
    public async Task ParallelLeaveAndHostRemove_OfOneAssignment_EndItOnce()
    {
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync(requiredCount: 10);
        var holder = (await FillAsync(occurrenceId, requirementId, 1))[0];

        var responses = await RaceAsync(
        [
            () => LeaveAsync(holder.Token, occurrenceId, holder.AssignmentId),
            () => SendAsync(HttpMethod.Post, $"{RosterUrl(occurrenceId)}/assignments/{holder.AssignmentId}/remove", hostToken)
        ]);

        responses.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        (await CodesAsync(responses)).Where(c => c is not null).Should().Equal(RosterConflictCodes.AssignmentEnded);
        var row = await FindAsync(holder.AssignmentId);
        row.Status.Should().Be(RosterAssignmentStatus.Cancelled);
        row.ExitReason.Should().BeOneOf(RosterExitReason.PlayerLeft, RosterExitReason.HostRemoved);
    }

    // ── refill ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task InProgressGame_ActivePlayerLeaves_ReplacementClaims_BackToTen()
    {
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync(requiredCount: 10);
        var holders = await FillAsync(occurrenceId, requirementId, 10);
        await SetEventStatusAsync(occurrenceId, EventStatus.InProgress);
        await ExecuteAsync($"UPDATE [RosterAssignments] SET [Status] = 4, [ActivatedAtUtc] = SYSUTCDATETIME() WHERE [OccurrenceId] = {occurrenceId}");

        var left = await LeaveAsync(holders[3].Token, occurrenceId, holders[3].AssignmentId);
        left.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = await SummaryAsync(occurrenceId);
        (summary.SupplyCount, summary.OpenQuantity, summary.IsRosterReady).Should().Be((9, 1, false));

        var replacementToken = (await NewPlayerTokensAsync(1))[0];
        var claim = await ClaimAsync(replacementToken, occurrenceId, new { requirementId, replacesAssignmentId = holders[3].AssignmentId });
        claim.StatusCode.Should().Be(HttpStatusCode.Created, await claim.Content.ReadAsStringAsync());

        summary = await SummaryAsync(occurrenceId);
        (summary.SupplyCount, summary.OpenQuantity, summary.IsRosterReady).Should().Be((10, 0, true));
        var original = await FindAsync(holders[3].AssignmentId);
        (original.Status, original.ExitReason).Should().Be((RosterAssignmentStatus.Departed, RosterExitReason.PlayerLeft));
        original.DepartedAtUtc.Should().NotBeNull();
        var replacement = (await claim.Content.ReadFromJsonAsync<RosterAssignmentDto>())!;
        replacement.ReplacedAssignmentId.Should().Be(holders[3].AssignmentId);
        _ = hostToken;
    }

    // ── 5-on-5 pickup basketball dogfood ─────────────────────────────────────

    [Fact]
    public async Task BasketballDogfood_WednesdayPickup_FillRaceLeaveRefill()
    {
        // Wednesday 8 PM, standalone pickup (no team), one participant requirement of 10.
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync(requiredCount: 10);
        (await _client.GetFromJsonAsync<EventDto>($"/api/v1/events/{occurrenceId}"))!.TeamId.Should().BeNull();

        // Step 1: the host's administrative role holds no spot.
        await ExpectSummaryAsync(occurrenceId, 0, 10, ready: false);

        // Step 2: the host plays, through the same endpoint as everyone else.
        (await ClaimAsync(hostToken, occurrenceId, new { requirementId })).StatusCode.Should().Be(HttpStatusCode.Created);
        await ExpectSummaryAsync(occurrenceId, 1, 9, ready: false);

        // Players with no team membership claim until 9/10.
        var players = await NewPlayerTokensAsync(10);
        var claimed = new Dictionary<string, Guid>();
        foreach (var token in players.Take(8))
        {
            var response = await ClaimAsync(token, occurrenceId, new { requirementId });
            response.StatusCode.Should().Be(HttpStatusCode.Created);
            claimed[token] = (await response.Content.ReadFromJsonAsync<RosterAssignmentDto>())!.Id;
        }
        await ExpectSummaryAsync(occurrenceId, 9, 1, ready: false);

        // Two players race for the final spot.
        var race = await RaceAsync(players.Skip(8).Take(2).Select(token => (Func<Task<HttpResponseMessage>>)(() =>
            ClaimAsync(token, occurrenceId, new { requirementId }))).ToList());
        race.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.Created, HttpStatusCode.Conflict]);
        (await CodesAsync(race)).Where(c => c is not null).Should().Equal(RosterConflictCodes.RequirementFull);
        var winnerIndex = race[0].StatusCode == HttpStatusCode.Created ? 0 : 1;
        var winnerToken = players[8 + winnerIndex];
        var winnerAssignment = (await race[winnerIndex].Content.ReadFromJsonAsync<RosterAssignmentDto>())!;
        await ExpectSummaryAsync(occurrenceId, 10, 0, ready: true);

        // The winner leaves: one spot reopens at once, history stays.
        var leave = await LeaveAsync(winnerToken, occurrenceId, winnerAssignment.Id);
        leave.StatusCode.Should().Be(HttpStatusCode.OK);
        var exited = (await leave.Content.ReadFromJsonAsync<RosterAssignmentDto>())!;
        (exited.Status, exited.ExitReason).Should().Be((RosterAssignmentStatus.Cancelled, RosterExitReason.PlayerLeft));
        await ExpectSummaryAsync(occurrenceId, 9, 1, ready: false);

        // A new player (not the race loser, to show anyone can refill) claims the reopened spot.
        var replacementToken = (await NewPlayerTokensAsync(1))[0];
        (await ClaimAsync(replacementToken, occurrenceId, new { requirementId })).StatusCode.Should().Be(HttpStatusCode.Created);
        var summary = await ExpectSummaryAsync(occurrenceId, 10, 0, ready: true);

        // The departed assignment is still there, as history, next to its replacement.
        summary.Assignments.Should().HaveCount(11);
        summary.Assignments.Single(a => a.Id == winnerAssignment.Id).Status.Should().Be(RosterAssignmentStatus.Cancelled);
        (await _db.CreateContext().TeamMembers.AnyAsync()).Should().BeFalse();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string RosterUrl(Guid occurrenceId) => $"/api/v1/events/{occurrenceId}/roster";

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

    private async Task<(string HostToken, Guid OccurrenceId, Guid RequirementId)> SeedPickupAsync(int requiredCount, string roleCode = "participant")
    {
        var hostId = Guid.NewGuid();
        var hostToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, hostId);

        await using (var context = _db.CreateContext())
        {
            var occurrence = new EventOccurrence
            {
                Id = Guid.NewGuid(),
                Name = "Wednesday 8 PM pickup basketball",
                ScheduledStartUtc = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc),
                Status = EventStatus.Open,
                MaxParticipants = 10,
                HostId = hostId,
                TeamId = null
            };
            context.Events.Add(occurrence);
            await context.SaveChangesAsync();

            var requirementId = await CreateRequirementAsync(hostToken, occurrence.Id, roleCode, requiredCount);
            return (hostToken, occurrence.Id, requirementId);
        }
    }

    private async Task<Guid> CreateRequirementAsync(string hostToken, Guid occurrenceId, string roleCode, int requiredCount)
    {
        var response = await SendAsync(HttpMethod.Post, $"{RosterUrl(occurrenceId)}/requirements", hostToken, new { roleCode, requiredCount });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RosterRequirementDto>())!.Id;
    }

    private async Task<List<string>> NewPlayerTokensAsync(int count)
    {
        var tokens = new List<string>();
        for (var i = 0; i < count; i++)
            tokens.Add(await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, Guid.NewGuid()));
        return tokens;
    }

    /// <summary>Self-claims <paramref name="count"/> spots with new linked players, sequentially.</summary>
    private async Task<List<(string Token, Guid AssignmentId)>> FillAsync(Guid occurrenceId, Guid requirementId, int count)
    {
        var holders = new List<(string, Guid)>();
        foreach (var token in await NewPlayerTokensAsync(count))
        {
            var response = await ClaimAsync(token, occurrenceId, new { requirementId });
            response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            holders.Add((token, (await response.Content.ReadFromJsonAsync<RosterAssignmentDto>())!.Id));
        }
        return holders;
    }

    private Task<HttpResponseMessage> ClaimAsync(string token, Guid occurrenceId, object body) =>
        SendAsync(HttpMethod.Post, $"{RosterUrl(occurrenceId)}/claims", token, body);

    private Task<HttpResponseMessage> LeaveAsync(string token, Guid occurrenceId, Guid assignmentId) =>
        SendAsync(HttpMethod.Post, $"{RosterUrl(occurrenceId)}/assignments/{assignmentId}/leave", token);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private async Task<RosterSummaryDto> SummaryAsync(Guid occurrenceId) =>
        (await _client.GetFromJsonAsync<RosterSummaryDto>(RosterUrl(occurrenceId)))!;

    private async Task<RosterSummaryDto> ExpectSummaryAsync(Guid occurrenceId, int supply, int open, bool ready)
    {
        var summary = await SummaryAsync(occurrenceId);
        (summary.RequiredCount, summary.SupplyCount, summary.OpenQuantity, summary.IsRosterReady)
            .Should().Be((10, supply, open, ready));
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

    private async Task SetEventStatusAsync(Guid occurrenceId, EventStatus status)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var occurrence = await db.Events.SingleAsync(e => e.Id == occurrenceId);
        occurrence.Status = status;
        await db.SaveChangesAsync();
    }

    private async Task ExecuteAsync(FormattableString sql)
    {
        await using var context = _db.CreateContext();
        await context.Database.ExecuteSqlAsync(sql);
    }
}
