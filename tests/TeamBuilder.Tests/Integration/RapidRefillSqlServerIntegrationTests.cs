using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Application.Outbox;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Domain.Outbox;
using TeamBuilder.Tests.Application;
using Xunit.Abstractions;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// The proactive refill loop over HTTP against a real, fully migrated SQL Server database:
/// "notify me" on a full game, a departure (leave / host remove / no-show) that stages a
/// vacancy outbox message in the same commit, the outbox processor revalidating and creating
/// in-app notifications, and the subscriber claiming through the existing atomic claim. Races
/// are released together by a gate (no sleeps); the processor is called directly.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class RapidRefillSqlServerIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _fixture;
    private readonly ITestOutputHelper _output;
    private SqlServerTestDatabase _db = null!;
    private SqlServerWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private RefillHarness _h = null!;

    public RapidRefillSqlServerIntegrationTests(SqlServerContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "refill");
        await _db.MigrateToAsync();
        _factory = new SqlServerWebApplicationFactory(_db.ConnectionString);
        _client = _factory.CreateClient();
        _h = new RefillHarness(_factory, _client, _db.CreateContext);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    // ── Part K: the first rapid-refill dogfood ───────────────────────────────

    [Fact]
    public async Task Dogfood_FullGame_NotifyMe_PlayerLeaves_WorkerNotifies_SubscriberClaims_BackToReady()
    {
        // HOST 1-4: Wednesday Basketball, participant x 10, filled to READY 10/10.
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 10);
        var players = await _h.FillAsync(occurrenceId, requirementId, 10);
        (await _h.DetailAsync(occurrenceId)).IsRosterReady.Should().BeTrue();

        // PLAYER 11, 5-8: opens the full game, cannot claim, presses "Notify me".
        var (player11Id, player11) = await _h.NewPlayerAsync();
        var full = await _h.DetailAsync(occurrenceId, player11);
        (full.SupplyCount, full.RequiredCount, full.MyAssignmentId).Should().Be((10, 10, null));
        var refused = await _h.ClaimAsync(player11, occurrenceId, requirementId);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RefillHarness.CodeAsync(refused)).Should().Be(RosterConflictCodes.RequirementFull);
        var subscribed = await _h.SubscribeAsync(player11, occurrenceId, requirementId);
        subscribed.StatusCode.Should().Be(HttpStatusCode.Created);
        (await _h.DetailAsync(occurrenceId, player11)).MySubscribedRequirementIds.Should().Equal(requirementId);

        // PLAYER 3, 9: leaves.
        var player3 = players[2];
        (await _h.LeaveAsync(player3.Token, occurrenceId, player3.AssignmentId)).StatusCode.Should().Be(HttpStatusCode.OK);

        // 10-13: the assignment is terminal, supply is 9/10 derived, one vacancy outbox row exists,
        // and nothing was delivered inside the request.
        await using (var context = _db.CreateContext())
        {
            var row = await context.RosterAssignments.AsNoTracking().SingleAsync(a => a.Id == player3.AssignmentId);
            (row.Status, row.ExitReason).Should().Be((RosterAssignmentStatus.Cancelled, RosterExitReason.PlayerLeft));
        }
        (await _h.LiveSupplyAsync(requirementId)).Should().Be(9);
        var outbox = (await _h.OutboxAsync(occurrenceId)).Should().ContainSingle().Subject;
        (outbox.Type, outbox.Status, outbox.AttemptCount).Should().Be((RosterVacancyOpenedV1.EventType, OutboxMessageStatus.Pending, 0));
        var vacancy = RosterVacancyOpenedV1.FromJson(outbox.PayloadJson);
        (vacancy.EventId, vacancy.OccurrenceId, vacancy.RosterRequirementId, vacancy.VacatedAssignmentId, vacancy.Reason)
            .Should().Be((outbox.Id, occurrenceId, requirementId, player3.AssignmentId, VacancyReason.PlayerLeft));
        (vacancy.PreviousOpenQuantity, vacancy.OpenQuantity, vacancy.RequiredCount, vacancy.RoleCode).Should().Be((0, 1, 10, "participant"));
        (await _h.NotificationRowsAsync(occurrenceId)).Should().BeEmpty("delivery is asynchronous, never inside the leave request");

        // WORKER 14-16: claims, revalidates, creates exactly one notification for player 11.
        var processor = _h.Processor();
        var batch = await processor.ProcessBatchAsync();
        (batch.Claimed, batch.Processed, batch.Skipped, batch.Failed).Should().Be((1, 1, 0, 0));
        var notification = (await _h.NotificationRowsAsync(occurrenceId)).Should().ContainSingle().Subject;
        (notification.PlayerId, notification.SourceEventId, notification.RosterRequirementId).Should().Be((player11Id, outbox.Id, requirementId));
        (notification.Title, notification.Body).Should().Be(("Basketball spot opened", "A participant spot opened in Wednesday Basketball."));
        (await _h.OutboxAsync(occurrenceId)).Single().Status.Should().Be(OutboxMessageStatus.Completed);

        // 17: reprocessing the same message does not duplicate it.
        await ResetToPendingAsync(outbox.Id);
        (await processor.ProcessBatchAsync()).Skipped.Should().Be(1);
        (await _h.NotificationRowsAsync(occurrenceId)).Should().ContainSingle();

        // PLAYER 11, 18-22: sees it unread, opens it, sees 9/10, claims, READY 10/10.
        (await _h.UnreadCountAsync(player11)).Should().Be(1);
        var inbox = await _h.NotificationsAsync(player11, unreadOnly: true);
        var item = inbox.Items.Should().ContainSingle().Subject;
        (item.OccurrenceId, item.IsRead).Should().Be((occurrenceId, false));
        (await _h.MarkReadAsync(player11, item.Id)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _h.UnreadCountAsync(player11)).Should().Be(0);
        var opened = await _h.DetailAsync(item.OccurrenceId, player11);
        (opened.SupplyCount, opened.OpenQuantity).Should().Be((9, 1));
        (await _h.ClaimAsync(player11, occurrenceId, requirementId)).StatusCode.Should().Be(HttpStatusCode.Created);
        var ready = await _h.DetailAsync(occurrenceId, player11);
        (ready.SupplyCount, ready.IsRosterReady, ready.MyAssignmentId.HasValue).Should().Be((10, true, true));

        // 23: later retries/processing produce no new alert for the filled vacancy.
        await ResetToPendingAsync(outbox.Id);
        var late = await processor.ProcessBatchAsync();
        (late.Processed, late.Skipped).Should().Be((0, 1));
        (await _h.NotificationRowsAsync(occurrenceId)).Should().ContainSingle();

        // No Team entity anywhere in the loop.
        await using var check = _db.CreateContext();
        (await check.Teams.AnyAsync()).Should().BeFalse();
        (await check.TeamMembers.AnyAsync()).Should().BeFalse();
    }

    // ── Part B/L: emission per mutation path ─────────────────────────────────

    [Theory]
    [InlineData("leave", VacancyReason.PlayerLeft)]
    [InlineData("remove", VacancyReason.HostRemoved)]
    [InlineData("no-show", VacancyReason.NoShow)]
    public async Task EachVacancyPath_StagesExactlyOneMessage_AndARepeatedRequestStagesNoMore(string action, VacancyReason reason)
    {
        var (_, hostToken, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 10);
        var holder = (await _h.FillAsync(occurrenceId, requirementId, 10))[4];

        var first = await ActAsync(action, hostToken, holder.Token, occurrenceId, holder.AssignmentId);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var repeated = await ActAsync(action, hostToken, holder.Token, occurrenceId, holder.AssignmentId);
        repeated.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RefillHarness.CodeAsync(repeated)).Should().Be(RosterConflictCodes.AssignmentEnded);

        var message = (await _h.OutboxAsync(occurrenceId)).Should().ContainSingle().Subject;
        message.DeduplicationKey.Should().Be(RosterVacancyOpenedV1.DeduplicationKeyFor(holder.AssignmentId));
        var vacancy = RosterVacancyOpenedV1.FromJson(message.PayloadJson);
        (vacancy.Reason, vacancy.PreviousOpenQuantity, vacancy.OpenQuantity).Should().Be((reason, 0, 1));
        (await _h.LiveSupplyAsync(requirementId)).Should().Be(9);
    }

    [Fact]
    public async Task CheckIn_Activate_Claim_AndHostAssignment_StageNothing()
    {
        var (_, hostToken, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 10);
        var holders = await _h.FillAsync(occurrenceId, requirementId, 5);
        (await _h.HostActAsync(hostToken, occurrenceId, holders[0].AssignmentId, "check-in")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _h.HostActAsync(hostToken, occurrenceId, holders[0].AssignmentId, "activate")).StatusCode.Should().Be(HttpStatusCode.OK);
        var (assigned, _) = await _h.NewPlayerAsync();
        (await _h.SendAsync(HttpMethod.Post, $"{RefillHarness.RosterUrl(occurrenceId)}/assignments", hostToken, new { playerId = assigned, requirementId }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        (await _h.OutboxAsync(occurrenceId)).Should().BeEmpty();
    }

    [Fact]
    public async Task ActivePlayerLeavingAnInProgressGame_OpensASpot()
    {
        var (_, hostToken, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 10);
        var holder = (await _h.FillAsync(occurrenceId, requirementId, 10))[0];
        await _h.SetEventStatusAsync(occurrenceId, EventStatus.InProgress);
        (await _h.HostActAsync(hostToken, occurrenceId, holder.AssignmentId, "check-in")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _h.HostActAsync(hostToken, occurrenceId, holder.AssignmentId, "activate")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _h.LeaveAsync(holder.Token, occurrenceId, holder.AssignmentId)).StatusCode.Should().Be(HttpStatusCode.OK);

        RosterVacancyOpenedV1.FromJson((await _h.OutboxAsync(occurrenceId)).Single().PayloadJson).Reason.Should().Be(VacancyReason.PlayerLeft);
    }

    [Theory]
    [InlineData(EventStatus.Completed)]
    [InlineData(EventStatus.Cancelled)]
    public async Task ClosedOccurrence_RefusesTheDeparture_AndStagesNothing(EventStatus closed)
    {
        var (_, hostToken, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 10);
        var holders = await _h.FillAsync(occurrenceId, requirementId, 3);
        await _h.SetEventStatusAsync(occurrenceId, closed);

        (await _h.LeaveAsync(holders[0].Token, occurrenceId, holders[0].AssignmentId)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _h.HostActAsync(hostToken, occurrenceId, holders[1].AssignmentId, "remove")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _h.HostActAsync(hostToken, occurrenceId, holders[2].AssignmentId, "no-show")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await _h.OutboxAsync(occurrenceId)).Should().BeEmpty();
    }

    [Fact]
    public async Task OverfilledRequirement_DepartureThatOpensNothing_StagesNothing_ThenTheOneThatDoesStagesOne()
    {
        // Legacy overfill (11 supply on 10 required) can only be written directly.
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 10);
        var holders = await _h.FillAsync(occurrenceId, requirementId, 10);
        var (extraPlayer, extraToken) = await _h.NewPlayerAsync();
        var extraAssignment = await InsertSupplyDirectlyAsync(occurrenceId, requirementId, extraPlayer);
        (await _h.LiveSupplyAsync(requirementId)).Should().Be(11);

        (await _h.LeaveAsync(extraToken, occurrenceId, extraAssignment)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _h.OutboxAsync(occurrenceId)).Should().BeEmpty("11 -> 10 of 10 opens nothing");

        (await _h.LeaveAsync(holders[0].Token, occurrenceId, holders[0].AssignmentId)).StatusCode.Should().Be(HttpStatusCode.OK);
        var vacancy = RosterVacancyOpenedV1.FromJson((await _h.OutboxAsync(occurrenceId)).Single().PayloadJson);
        (vacancy.PreviousOpenQuantity, vacancy.OpenQuantity).Should().Be((0, 1));
    }

    [Fact]
    public async Task OverfilledRequirement_TwoSimultaneousDepartures_StageExactlyOneVacancy()
    {
        // 12 on 10 required: two departures at once reach 10 (nothing opens); a third reaches 9.
        // Racing departures of an overfilled requirement are serialized by its RowVersion, so
        // no stale count can lose or invent the vacancy.
        var (_, hostToken, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 10);
        var holders = await _h.FillAsync(occurrenceId, requirementId, 10);
        for (var i = 0; i < 2; i++)
        {
            var (playerId, _) = await _h.NewPlayerAsync();
            await InsertSupplyDirectlyAsync(occurrenceId, requirementId, playerId);
        }

        var responses = await RefillHarness.RaceAsync(
        [
            () => _h.LeaveAsync(holders[0].Token, occurrenceId, holders[0].AssignmentId),
            () => _h.HostActAsync(hostToken, occurrenceId, holders[1].AssignmentId, "remove"),
            () => _h.HostActAsync(hostToken, occurrenceId, holders[2].AssignmentId, "no-show")
        ]);

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        (await _h.LiveSupplyAsync(requirementId)).Should().Be(9);
        var vacancy = RosterVacancyOpenedV1.FromJson((await _h.OutboxAsync(occurrenceId)).Should().ContainSingle().Subject.PayloadJson);
        (vacancy.PreviousOpenQuantity, vacancy.OpenQuantity).Should().Be((0, 1));
    }

    // ── Part M: concurrency of terminal transitions ──────────────────────────

    [Fact]
    public async Task LeavesOfDifferentAssignments_AtOnce_StageOneMessageEach()
    {
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 10);
        var holders = await _h.FillAsync(occurrenceId, requirementId, 10);

        var responses = await RefillHarness.RaceAsync(holders.Take(5).Select(h => (Func<Task<HttpResponseMessage>>)(() =>
            _h.LeaveAsync(h.Token, occurrenceId, h.AssignmentId))));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        var messages = await _h.OutboxAsync(occurrenceId);
        messages.Should().HaveCount(5);
        messages.Select(m => RosterVacancyOpenedV1.FromJson(m.PayloadJson).VacatedAssignmentId)
            .Should().BeEquivalentTo(holders.Take(5).Select(h => h.AssignmentId));
        (await _h.LiveSupplyAsync(requirementId)).Should().Be(5);
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("no-show")]
    public async Task HostTerminalTransition_RacingTheHoldersLeave_StagesExactlyOneMessage(string hostAction)
    {
        var (_, hostToken, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 10);
        var holder = (await _h.FillAsync(occurrenceId, requirementId, 10))[0];

        var responses = await RefillHarness.RaceAsync(
        [
            () => _h.LeaveAsync(holder.Token, occurrenceId, holder.AssignmentId),
            () => _h.HostActAsync(hostToken, occurrenceId, holder.AssignmentId, hostAction),
            () => _h.HostActAsync(hostToken, occurrenceId, holder.AssignmentId, hostAction)
        ]);

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        foreach (var loser in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            loser.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await RefillHarness.CodeAsync(loser)).Should().BeOneOf(RosterConflictCodes.AssignmentEnded, RosterConflictCodes.RosterChanged);
        }

        var message = (await _h.OutboxAsync(occurrenceId)).Should().ContainSingle().Subject;
        RosterVacancyOpenedV1.FromJson(message.PayloadJson).VacatedAssignmentId.Should().Be(holder.AssignmentId);
        (await _h.LiveSupplyAsync(requirementId)).Should().Be(9);
    }

    [Fact]
    public async Task VacancyRacingAnImmediateRefill_NeverOverbooks_AndNotifiesOnlyIfStillOpen()
    {
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 10);
        var holders = await _h.FillAsync(occurrenceId, requirementId, 10);
        var (_, subscriber) = await _h.NewPlayerAsync();
        (await _h.SubscribeAsync(subscriber, occurrenceId, requirementId)).StatusCode.Should().Be(HttpStatusCode.Created);
        var claimants = new List<string>();
        for (var i = 0; i < 3; i++)
            claimants.Add((await _h.NewPlayerAsync()).Token);

        var operations = new List<Func<Task<HttpResponseMessage>>> { () => _h.LeaveAsync(holders[0].Token, occurrenceId, holders[0].AssignmentId) };
        operations.AddRange(claimants.Select(token => (Func<Task<HttpResponseMessage>>)(() => _h.ClaimAsync(token, occurrenceId, requirementId))));
        var responses = await RefillHarness.RaceAsync(operations);

        responses[0].StatusCode.Should().Be(HttpStatusCode.OK);
        var refilled = responses.Skip(1).Count(r => r.StatusCode == HttpStatusCode.Created);
        refilled.Should().BeLessThanOrEqualTo(1);
        (await _h.LiveSupplyAsync(requirementId)).Should().Be(9 + refilled);
        (await _h.OutboxAsync(occurrenceId)).Should().ContainSingle("the committed vacancy is never lost");

        var batch = await _h.Processor().ProcessBatchAsync();
        var notifications = await _h.NotificationRowsAsync(occurrenceId);
        if (refilled == 1)
        {
            (batch.Skipped, notifications.Count).Should().Be((1, 0));
        }
        else
        {
            (batch.Processed, notifications.Count).Should().Be((1, 1));
        }
        _output.WriteLine($"leave vs 3 claims: {refilled} immediate refill(s), {notifications.Count} notification(s).");
    }

    // ── Part F/M: process-time revalidation ──────────────────────────────────

    [Fact]
    public async Task MultipleSubscribers_EachGetExactlyOne_AndTheLeaverAndParticipantsGetNone()
    {
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 4);
        var holders = await _h.FillAsync(occurrenceId, requirementId, 4);
        var subscribers = new List<(Guid PlayerId, string Token)>();
        for (var i = 0; i < 5; i++)
        {
            var subscriber = await _h.NewPlayerAsync();
            (await _h.SubscribeAsync(subscriber.Token, occurrenceId, requirementId)).StatusCode.Should().Be(HttpStatusCode.Created);
            subscribers.Add(subscriber);
        }

        // A subscriber that later left the game is still subscribed (subscribed before joining):
        // it must not be told about the spot it just gave up.
        var leaver = holders[0];
        await using (var context = _db.CreateContext())
        {
            context.OccurrenceRosterSubscriptions.Add(new OccurrenceRosterSubscription
            {
                Id = Guid.NewGuid(),
                PlayerId = leaver.PlayerId,
                OccurrenceId = occurrenceId,
                RosterRequirementId = requirementId
            });
            context.OccurrenceRosterSubscriptions.Add(new OccurrenceRosterSubscription
            {
                Id = Guid.NewGuid(),
                PlayerId = holders[1].PlayerId,
                OccurrenceId = occurrenceId,
                RosterRequirementId = requirementId
            });
            await context.SaveChangesAsync();
        }

        (await _h.LeaveAsync(leaver.Token, occurrenceId, leaver.AssignmentId)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _h.Processor().ProcessBatchAsync()).Processed.Should().Be(1);

        var notifications = await _h.NotificationRowsAsync(occurrenceId);
        notifications.Select(n => n.PlayerId).Should().BeEquivalentTo(subscribers.Select(s => s.PlayerId));
        foreach (var (_, token) in subscribers)
            (await _h.UnreadCountAsync(token)).Should().Be(1);
        (await _h.UnreadCountAsync(leaver.Token)).Should().Be(0);
        (await _h.UnreadCountAsync(holders[1].Token)).Should().Be(0);
    }

    [Fact]
    public async Task SubscriberClaimsBeforeTheWorkerRuns_IsNotNotified_AndAFullRequirementSkipsEveryone()
    {
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 10);
        var holders = await _h.FillAsync(occurrenceId, requirementId, 10);
        var (_, fast) = await _h.NewPlayerAsync();
        var (_, slow) = await _h.NewPlayerAsync();
        await _h.SubscribeAsync(fast, occurrenceId, requirementId);
        await _h.SubscribeAsync(slow, occurrenceId, requirementId);

        (await _h.LeaveAsync(holders[0].Token, occurrenceId, holders[0].AssignmentId)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _h.ClaimAsync(fast, occurrenceId, requirementId)).StatusCode.Should().Be(HttpStatusCode.Created);

        var batch = await _h.Processor().ProcessBatchAsync();
        (batch.Processed, batch.Skipped).Should().Be((0, 1));
        (await _h.NotificationRowsAsync(occurrenceId)).Should().BeEmpty();
        (await _h.OutboxAsync(occurrenceId)).Single().Status.Should().Be(OutboxMessageStatus.Completed);
    }

    [Fact]
    public async Task SubscriberAlreadyPlaying_IsSkipped_WhileOthersAreNotified()
    {
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 10);
        var holders = await _h.FillAsync(occurrenceId, requirementId, 8);
        var (_, early) = await _h.NewPlayerAsync();
        var (lateId, late) = await _h.NewPlayerAsync();
        await _h.SubscribeAsync(early, occurrenceId, requirementId);
        await _h.SubscribeAsync(late, occurrenceId, requirementId);
        (await _h.ClaimAsync(early, occurrenceId, requirementId)).StatusCode.Should().Be(HttpStatusCode.Created);

        (await _h.LeaveAsync(holders[0].Token, occurrenceId, holders[0].AssignmentId)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _h.Processor().ProcessBatchAsync()).Processed.Should().Be(1);

        (await _h.NotificationRowsAsync(occurrenceId)).Select(n => n.PlayerId).Should().Equal(lateId);
    }

    [Theory]
    [InlineData(EventStatus.Cancelled)]
    [InlineData(EventStatus.Completed)]
    public async Task OccurrenceClosesBeforeTheWorkerRuns_NoNotification_MessageCompleted(EventStatus closed)
    {
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 10);
        var holders = await _h.FillAsync(occurrenceId, requirementId, 10);
        var (_, subscriber) = await _h.NewPlayerAsync();
        await _h.SubscribeAsync(subscriber, occurrenceId, requirementId);
        (await _h.LeaveAsync(holders[0].Token, occurrenceId, holders[0].AssignmentId)).StatusCode.Should().Be(HttpStatusCode.OK);

        await _h.SetEventStatusAsync(occurrenceId, closed);
        var batch = await _h.Processor().ProcessBatchAsync();

        (batch.Skipped, batch.Processed).Should().Be((1, 0));
        (await _h.NotificationRowsAsync(occurrenceId)).Should().BeEmpty();
        (await _h.OutboxAsync(occurrenceId)).Single().Status.Should().Be(OutboxMessageStatus.Completed);
    }

    [Fact]
    public async Task UnsubscribedOrDeletedSubscriber_BeforeTheWorkerRuns_IsNotNotified()
    {
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 10);
        var holders = await _h.FillAsync(occurrenceId, requirementId, 10);
        var (_, changedMind) = await _h.NewPlayerAsync();
        var (goneId, gone) = await _h.NewPlayerAsync();
        await _h.SubscribeAsync(changedMind, occurrenceId, requirementId);
        await _h.SubscribeAsync(gone, occurrenceId, requirementId);
        (await _h.LeaveAsync(holders[0].Token, occurrenceId, holders[0].AssignmentId)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _h.UnsubscribeAsync(changedMind, occurrenceId, requirementId)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using (var context = _db.CreateContext())
        {
            // Player deletion cascades their identities' links and subscriptions.
            await context.PlayerIdentities.Where(i => i.PlayerId == goneId).ExecuteDeleteAsync();
            await context.Players.Where(p => p.Id == goneId).ExecuteDeleteAsync();
            (await context.OccurrenceRosterSubscriptions.AnyAsync(s => s.PlayerId == goneId)).Should().BeFalse();
        }

        (await _h.Processor().ProcessBatchAsync()).Skipped.Should().Be(1);
        (await _h.NotificationRowsAsync(occurrenceId)).Should().BeEmpty();
    }

    // ── Part G: subscription rules ───────────────────────────────────────────

    [Fact]
    public async Task Subscribe_IsIdempotent_AndUnsubscribe_IsSafeTwice()
    {
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 2);
        await _h.FillAsync(occurrenceId, requirementId, 2);
        var (playerId, token) = await _h.NewPlayerAsync();

        var responses = await RefillHarness.RaceAsync(Enumerable.Range(0, 5).Select(_ => (Func<Task<HttpResponseMessage>>)(() =>
            _h.SubscribeAsync(token, occurrenceId, requirementId))));
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.OK);
        var body = (await responses[0].Content.ReadFromJsonAsync<RosterSubscriptionDto>())!;
        (body.Subscribed, body.OccurrenceId, body.RosterRequirementId).Should().Be((true, occurrenceId, requirementId));
        await using (var context = _db.CreateContext())
            (await context.OccurrenceRosterSubscriptions.CountAsync(s => s.PlayerId == playerId)).Should().Be(1);

        (await _h.UnsubscribeAsync(token, occurrenceId, requirementId)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _h.UnsubscribeAsync(token, occurrenceId, requirementId)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _h.DetailAsync(occurrenceId, token)).MySubscribedRequirementIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Subscribe_RejectsParticipants_ForeignRequirements_AndClosedGames()
    {
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 10);
        var holder = (await _h.FillAsync(occurrenceId, requirementId, 1))[0];
        var otherRequirement = (await _h.SeedGameAsync(requiredCount: 5)).RequirementId;
        var (_, outsider) = await _h.NewPlayerAsync();

        var playing = await _h.SubscribeAsync(holder.Token, occurrenceId, requirementId);
        playing.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RefillHarness.CodeAsync(playing)).Should().Be(RosterConflictCodes.AlreadyParticipating);

        (await _h.SubscribeAsync(outsider, occurrenceId, otherRequirement)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _h.SubscribeAsync(outsider, Guid.NewGuid(), requirementId)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _h.UnsubscribeAsync(outsider, occurrenceId, otherRequirement)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        await _h.SetEventStatusAsync(occurrenceId, EventStatus.Cancelled);
        var closed = await _h.SubscribeAsync(outsider, occurrenceId, requirementId);
        closed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RefillHarness.CodeAsync(closed)).Should().Be(RosterConflictCodes.OccurrenceClosed);
        (await _h.UnsubscribeAsync(outsider, occurrenceId, requirementId)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task MultiRoleGame_SubscriptionIsPerRequirement_AndOnlyThatRolesSubscribersHear()
    {
        var (_, hostToken, occurrenceId, tank) = await _h.SeedGameAsync(requiredCount: 1, name: "Raid night", roleCode: "tank");
        var healer = await _h.CreateRequirementAsync(hostToken, occurrenceId, "healer", 1);
        var tankHolder = (await _h.FillAsync(occurrenceId, tank, 1))[0];
        await _h.FillAsync(occurrenceId, healer, 1);
        var (tankFanId, tankFan) = await _h.NewPlayerAsync();
        var (_, healerFan) = await _h.NewPlayerAsync();
        await _h.SubscribeAsync(tankFan, occurrenceId, tank);
        await _h.SubscribeAsync(healerFan, occurrenceId, healer);

        (await _h.LeaveAsync(tankHolder.Token, occurrenceId, tankHolder.AssignmentId)).StatusCode.Should().Be(HttpStatusCode.OK);
        await _h.Processor().ProcessBatchAsync();

        var notification = (await _h.NotificationRowsAsync(occurrenceId)).Should().ContainSingle().Subject;
        (notification.PlayerId, notification.RosterRequirementId, notification.Body).Should().Be((tankFanId, tank, "A tank spot opened in Raid night."));
    }

    // ── Part I: notification API ─────────────────────────────────────────────

    [Fact]
    public async Task Notifications_AreNewestFirst_KeysetPaged_FilterUnread_AndPrivate()
    {
        var (_, token) = await _h.NewPlayerAsync();
        var (_, stranger) = await _h.NewPlayerAsync();
        var games = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 1, name: $"Game {i}");
            var holder = (await _h.FillAsync(occurrenceId, requirementId, 1))[0];
            await _h.SubscribeAsync(token, occurrenceId, requirementId);
            (await _h.LeaveAsync(holder.Token, occurrenceId, holder.AssignmentId)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await _h.Processor().ProcessBatchAsync()).Processed.Should().Be(1);
            games.Add(occurrenceId);
        }

        var first = await _h.NotificationsAsync(token, pageSize: 2);
        var second = await _h.NotificationsAsync(token, pageSize: 2, cursor: first.NextCursor);
        var third = await _h.NotificationsAsync(token, pageSize: 2, cursor: second.NextCursor);
        third.NextCursor.Should().BeNull();
        var all = first.Items.Concat(second.Items).Concat(third.Items).ToList();
        all.Select(n => n.OccurrenceId).Should().Equal(Enumerable.Reverse(games));
        all.Select(n => n.CreatedAtUtc).Should().BeInDescendingOrder();

        (await _h.MarkReadAsync(token, all[0].Id)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _h.MarkReadAsync(token, all[0].Id)).StatusCode.Should().Be(HttpStatusCode.NoContent, "mark-read is idempotent");
        (await _h.NotificationsAsync(token, unreadOnly: true)).Items.Select(n => n.Id).Should().Equal(all.Skip(1).Select(n => n.Id));
        (await _h.UnreadCountAsync(token)).Should().Be(4);

        (await _h.NotificationsAsync(stranger)).Items.Should().BeEmpty();
        (await _h.MarkReadAsync(stranger, all[1].Id)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _h.UnreadCountAsync(token)).Should().Be(4, "another player cannot mark it read");

        var badCursor = await _h.SendAsync(HttpMethod.Get, "/api/v1/players/me/notifications?cursor=not-a-cursor", token);
        badCursor.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task VacancyPayloadAndNotification_CarryNoPersonalData()
    {
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 2);
        var holder = (await _h.FillAsync(occurrenceId, requirementId, 2))[0];
        var (_, subscriber) = await _h.NewPlayerAsync();
        await _h.SubscribeAsync(subscriber, occurrenceId, requirementId);
        string username, identitySubject;
        await using (var context = _db.CreateContext())
        {
            username = await context.Players.Where(p => p.Id == holder.PlayerId).Select(p => p.Username).SingleAsync();
            identitySubject = await context.PlayerIdentities.Where(i => i.PlayerId == holder.PlayerId).Select(i => i.Subject).SingleAsync();
        }

        (await _h.LeaveAsync(holder.Token, occurrenceId, holder.AssignmentId)).StatusCode.Should().Be(HttpStatusCode.OK);
        await _h.Processor().ProcessBatchAsync();

        var payload = (await _h.OutboxAsync(occurrenceId)).Single().PayloadJson;
        using (var document = JsonDocument.Parse(payload))
        {
            document.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
                "eventId", "occurrenceId", "rosterRequirementId", "roleCode", "vacatedAssignmentId", "reason",
                "previousOpenQuantity", "openQuantity", "requiredCount", "occurrenceStartUtc", "venueId", "occurredAtUtc");
        }
        payload.Should().NotContain(username).And.NotContain(identitySubject).And.NotContain(holder.PlayerId.ToString());

        var notification = (await _h.NotificationsAsync(subscriber)).Items.Single();
        $"{notification.Title} {notification.Body}".Should().NotContain(username).And.NotContain("left").And.NotContain("removed");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> ActAsync(string action, string hostToken, string holderToken, Guid occurrenceId, Guid assignmentId) =>
        action == "leave"
            ? _h.LeaveAsync(holderToken, occurrenceId, assignmentId)
            : _h.HostActAsync(hostToken, occurrenceId, assignmentId, action);

    private async Task<Guid> InsertSupplyDirectlyAsync(Guid occurrenceId, Guid requirementId, Guid playerId)
    {
        await using var context = _db.CreateContext();
        var assignment = new RosterAssignment
        {
            Id = Guid.NewGuid(),
            OccurrenceId = occurrenceId,
            PlayerId = playerId,
            RequirementId = requirementId,
            RoleCode = "participant",
            Status = RosterAssignmentStatus.Confirmed,
            Source = RosterAssignmentSource.Host,
            ConfirmedAtUtc = DateTime.UtcNow
        };
        context.RosterAssignments.Add(assignment);
        await context.SaveChangesAsync();
        return assignment.Id;
    }

    private async Task ResetToPendingAsync(Guid messageId)
    {
        await using var context = _db.CreateContext();
        await context.OutboxMessages.Where(m => m.Id == messageId).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, OutboxMessageStatus.Pending)
            .SetProperty(m => m.AttemptCount, 0)
            .SetProperty(m => m.NextAttemptAtUtc, DateTime.UtcNow.AddSeconds(-1)));
    }
}
