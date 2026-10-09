using System.Net;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Application.Outbox;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Outbox;
using TeamBuilder.Infrastructure.Services;
using TeamBuilder.Tests.Integration;
using Xunit.Abstractions;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// The transactional outbox on a real SQL Server 2022 database: the roster change and its
/// vacancy message commit or roll back together, and the worker's claim/lease/complete cycle is
/// exclusive across concurrent workers, survives a crashed worker, retries with backoff, gives
/// up after MaxAttempts, and is at-least-once with idempotent notifications. Time is a
/// settable clock and processing is called directly: no sleeps.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class OutboxSqlServerIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _fixture;
    private readonly ITestOutputHelper _output;
    private SqlServerTestDatabase _db = null!;
    private SqlServerWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private RefillHarness _h = null!;

    public OutboxSqlServerIntegrationTests(SqlServerContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "outbox");
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

    // ── Part C: atomicity ────────────────────────────────────────────────────

    [Fact]
    public async Task Leave_WhenTheOutboxInsertFails_RollsBackTheAssignmentToo()
    {
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 10);
        var holder = (await _h.FillAsync(occurrenceId, requirementId, 10))[0];
        await FailEveryOutboxInsertAsync();

        await using (var context = _db.CreateContext())
        {
            var service = new EventRosterService(context, TimeProvider.System);
            var act = () => service.LeaveAsync(occurrenceId, holder.AssignmentId, holder.PlayerId);
            (await act.Should().ThrowAsync<DbUpdateException>()).Which.InnerException.Should().BeOfType<SqlException>()
                .Which.Number.Should().Be(OutboxInsertFailure);
        }

        await using var check = _db.CreateContext();
        var row = await check.RosterAssignments.AsNoTracking().SingleAsync(a => a.Id == holder.AssignmentId);
        (row.Status, row.ExitReason, row.DepartedAtUtc).Should().Be((RosterAssignmentStatus.Confirmed, (RosterExitReason?)null, (DateTime?)null));
        (await _h.LiveSupplyAsync(requirementId)).Should().Be(10);
        (await check.OutboxMessages.CountAsync(m => m.AggregateId == occurrenceId)).Should().Be(0, "the failed vacancy INSERT left nothing behind");
    }

    [Theory]
    [InlineData(RosterAssignmentTransition.NoShow)]
    [InlineData(null)]
    public async Task HostRemoveOrNoShow_WhenTheOutboxInsertFails_RollsBackTheOccurrenceGuardAndTheAssignment(RosterAssignmentTransition? transition)
    {
        var (hostId, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 10);
        var holder = (await _h.FillAsync(occurrenceId, requirementId, 10))[0];
        await FailEveryOutboxInsertAsync();
        var rowVersionBefore = await OccurrenceRowVersionAsync(occurrenceId);

        await using (var context = _db.CreateContext())
        {
            var service = new EventRosterService(context, TimeProvider.System);
            Func<Task> act = transition is { } t
                ? () => service.TransitionAsync(occurrenceId, holder.AssignmentId, hostId, t)
                : () => service.RemoveAsync(occurrenceId, holder.AssignmentId, hostId);
            (await act.Should().ThrowAsync<DbUpdateException>()).Which.InnerException.Should().BeOfType<SqlException>()
                .Which.Number.Should().Be(OutboxInsertFailure);
        }

        (await OccurrenceRowVersionAsync(occurrenceId)).Should().Equal(rowVersionBefore, "the occurrence authority UPDATE rolled back");
        await using var check = _db.CreateContext();
        (await check.RosterAssignments.AsNoTracking().SingleAsync(a => a.Id == holder.AssignmentId)).Status.Should().Be(RosterAssignmentStatus.Confirmed);
        (await check.OutboxMessages.CountAsync(m => m.AggregateId == occurrenceId)).Should().Be(0);
    }

    [Fact]
    public async Task Leave_WhenTheVacancyKeyIsAlreadyTaken_RollsBack_AndReportsAConflictNotA500()
    {
        // Only a concurrent end of the same assignment can own its vacancy key; the leave is
        // re-validated and, since the assignment is still live here, ends as RosterChanged.
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 10);
        var holder = (await _h.FillAsync(occurrenceId, requirementId, 10))[0];
        await InsertConflictingOutboxRowAsync(holder.AssignmentId);

        var response = await _h.LeaveAsync(holder.Token, occurrenceId, holder.AssignmentId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RefillHarness.CodeAsync(response)).Should().Be(TeamBuilder.Application.Exceptions.RosterConflictCodes.RosterChanged);
        (await _h.LiveSupplyAsync(requirementId)).Should().Be(10);
    }

    [Fact]
    public async Task HostRemove_RacingAHostTransfer_EitherCommitsWithItsMessageOrNeither()
    {
        for (var round = 0; round < 5; round++)
        {
            var (_, hostToken, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 10);
            var holders = await _h.FillAsync(occurrenceId, requirementId, 10);
            var (newHostId, _) = await _h.NewPlayerAsync();

            var responses = await RefillHarness.RaceAsync(
            [
                () => _h.HostActAsync(hostToken, occurrenceId, holders[0].AssignmentId, "remove"),
                () => _h.SendAsync(HttpMethod.Post, $"/api/v1/events/{occurrenceId}/host/transfer", hostToken, new { newHostPlayerId = newHostId })
            ]);

            var removed = responses[0].StatusCode == HttpStatusCode.OK;
            if (!removed)
                responses[0].StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Conflict);
            (await _h.OutboxAsync(occurrenceId)).Should().HaveCount(removed ? 1 : 0, "no phantom vacancy for a rolled-back removal");
            (await _h.LiveSupplyAsync(requirementId)).Should().Be(removed ? 9 : 10);
            _output.WriteLine($"round {round}: remove {(int)responses[0].StatusCode}, transfer {(int)responses[1].StatusCode}");
        }
    }

    // ── Part E/M: worker claiming, leases and retries ────────────────────────

    [Fact]
    public async Task TwoWorkers_ClaimingAtOnce_GetDisjointBatches()
    {
        await InsertMessagesAsync(40, "test.noop.v1");
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        var a = _h.Processor(clock, o => o.BatchSize = 25);
        var b = _h.Processor(clock, o => o.BatchSize = 25);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var racing = new[] { a, b }
            .Select(p => Task.Run(async () => { await gate.Task; return await p.ClaimBatchAsync(CancellationToken.None); }))
            .ToList();
        gate.SetResult();
        var claims = await Task.WhenAll(racing);

        // READPAST may skip rows another claimer is locking at that instant, so a racing claim
        // can come back short; what must hold is that no row is claimed twice and that the
        // skipped ones are still claimable.
        var drained = await _h.Processor(clock, o => o.BatchSize = 100).ClaimBatchAsync(CancellationToken.None);
        var all = claims.Append(drained).ToList();
        var ids = all.SelectMany(c => c.Messages.Select(m => m.Id)).ToList();
        ids.Should().OnlyHaveUniqueItems().And.HaveCount(40);
        await using var context = _db.CreateContext();
        var rows = await context.OutboxMessages.AsNoTracking().ToListAsync();
        rows.Should().OnlyContain(m => m.Status == OutboxMessageStatus.Processing && m.AttemptCount == 1);
        foreach (var claim in all)
            rows.Where(r => claim.Messages.Any(m => m.Id == r.Id)).Should().OnlyContain(r => r.LockOwner == claim.LockOwner);
        _output.WriteLine($"racing claims: {string.Join(", ", claims.Select(c => c.Messages.Count))}; drained afterwards: {drained.Messages.Count}");
    }

    [Fact]
    public async Task TwoWorkers_ProcessingTheSameVacancyAtOnce_NotifyEachSubscriberOnce()
    {
        var (occurrenceId, subscribers) = await VacancyWithSubscribersAsync(3);

        var a = _h.Processor();
        var b = _h.Processor();
        var results = await Task.WhenAll(Task.Run(() => a.ProcessBatchAsync()), Task.Run(() => b.ProcessBatchAsync()));

        results.Sum(r => r.Claimed).Should().Be(1);
        (await _h.NotificationRowsAsync(occurrenceId)).Select(n => n.PlayerId).Should().BeEquivalentTo(subscribers);
        (await _h.OutboxAsync(occurrenceId)).Single().Status.Should().Be(OutboxMessageStatus.Completed);
    }

    [Fact]
    public async Task CrashedWorker_ItsLeaseExpires_AnotherWorkerReclaims_AndALateDuplicateCreatesNothing()
    {
        var (occurrenceId, subscribers) = await VacancyWithSubscribersAsync(2);
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        var clock = new FixedTimeProvider(now);

        // Worker A claims and "crashes": it never processes or completes.
        var crashed = await _h.Processor(clock).ClaimBatchAsync(CancellationToken.None);
        crashed.Messages.Should().ContainSingle();

        // While the lease holds, nobody else can take it.
        var b = _h.Processor(clock);
        (await b.ProcessBatchAsync()).Claimed.Should().Be(0);

        // After the lease, B reclaims (attempt 2) and completes.
        clock.UtcNow = now.AddMinutes(1).AddSeconds(1);
        var reclaimed = await b.ProcessBatchAsync();
        (reclaimed.Claimed, reclaimed.Processed).Should().Be((1, 1));
        var message = (await _h.OutboxAsync(occurrenceId)).Single();
        (message.Status, message.AttemptCount, message.LockOwner).Should().Be((OutboxMessageStatus.Completed, 2, (string?)null));

        // A "wakes up" and runs its handler anyway: the unique (SourceEventId, PlayerId) absorbs it.
        var late = await HandleDirectlyAsync(crashed.Messages.Single());
        (late.Effects, late.Outcome).Should().Be((0, "AlreadyNotified"));
        (await _h.NotificationRowsAsync(occurrenceId)).Select(n => n.PlayerId).Should().BeEquivalentTo(subscribers);
    }

    [Fact]
    public async Task NotificationsCommittedButCompletionLost_TheRetryCreatesNoDuplicate()
    {
        var (occurrenceId, subscribers) = await VacancyWithSubscribersAsync(2);
        await using (var context = _db.CreateContext())
        {
            // The handler committed its notifications, then the worker died before completing.
            var message = await context.OutboxMessages.AsNoTracking().SingleAsync(m => m.AggregateId == occurrenceId);
            (await HandleDirectlyAsync(message)).Effects.Should().Be(2);
        }

        var retry = await _h.Processor().ProcessBatchAsync();
        (retry.Claimed, retry.Skipped, retry.Processed).Should().Be((1, 1, 0));
        (await _h.NotificationRowsAsync(occurrenceId)).Should().HaveCount(subscribers.Count);
        (await _h.OutboxAsync(occurrenceId)).Single().Status.Should().Be(OutboxMessageStatus.Completed);
    }

    [Fact]
    public async Task FailingMessage_BacksOff_WithoutBusyRetry_ThenFailsAfterMaxAttempts()
    {
        var id = (await InsertMessagesAsync(1, RosterVacancyOpenedV1.EventType, payload: "{ not json"))[0];
        var t0 = DateTimeOffset.UtcNow.AddSeconds(1);
        var clock = new FixedTimeProvider(t0);
        var processor = _h.Processor(clock, o =>
        {
            o.MaxAttempts = 3;
            o.RetryBaseDelay = TimeSpan.FromSeconds(5);
        });

        (await processor.ProcessBatchAsync()).Failed.Should().Be(1);
        var afterFirst = await MessageAsync(id);
        (afterFirst.Status, afterFirst.AttemptCount, afterFirst.LockOwner).Should().Be((OutboxMessageStatus.Pending, 1, (string?)null));
        afterFirst.LastError.Should().StartWith("JsonException");
        afterFirst.NextAttemptAtUtc.Should().BeCloseTo(t0.UtcDateTime.AddSeconds(5), TimeSpan.FromMilliseconds(10));

        (await processor.ProcessBatchAsync()).Claimed.Should().Be(0, "a backed-off message is not retried before its time");

        clock.UtcNow = t0.AddSeconds(5);
        (await processor.ProcessBatchAsync()).Failed.Should().Be(1);
        (await MessageAsync(id)).NextAttemptAtUtc.Should().BeCloseTo(t0.UtcDateTime.AddSeconds(15), TimeSpan.FromMilliseconds(10));

        clock.UtcNow = t0.AddSeconds(15);
        (await processor.ProcessBatchAsync()).Failed.Should().Be(1);
        var final = await MessageAsync(id);
        (final.Status, final.AttemptCount).Should().Be((OutboxMessageStatus.Failed, 3));
        final.ProcessedAtUtc.Should().NotBeNull();

        clock.UtcNow = t0.AddHours(1);
        (await processor.ProcessBatchAsync()).Claimed.Should().Be(0);
    }

    [Fact]
    public async Task UnknownMessageType_FailsAtOnce()
    {
        var id = (await InsertMessagesAsync(1, "test.unknown.v1"))[0];

        (await _h.Processor().ProcessBatchAsync()).Failed.Should().Be(1);

        var message = await MessageAsync(id);
        (message.Status, message.AttemptCount).Should().Be((OutboxMessageStatus.Failed, 1));
        message.LastError.Should().Contain("No handler");
    }

    [Fact]
    public async Task FinalAttemptLeaseExpiry_MarksTheMessageFailed()
    {
        var id = (await InsertMessagesAsync(1, RosterVacancyOpenedV1.EventType))[0];
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        var clock = new FixedTimeProvider(now);
        var processor = _h.Processor(clock, o => o.MaxAttempts = 1);

        (await processor.ClaimBatchAsync(CancellationToken.None)).Messages.Should().ContainSingle();
        clock.UtcNow = now.AddMinutes(2);
        (await processor.ProcessBatchAsync()).Claimed.Should().Be(0);

        var message = await MessageAsync(id);
        message.Status.Should().Be(OutboxMessageStatus.Failed);
        message.LastError.Should().Contain("lease");
    }

    [Fact]
    public async Task ReadCommittedSnapshotDatabase_ClaimsStayExclusive()
    {
        // Azure SQL Database runs with READ_COMMITTED_SNAPSHOT ON; UPDLOCK/READPAST still lock.
        await using (var connection = new SqlConnection(new SqlConnectionStringBuilder(_db.ConnectionString) { InitialCatalog = "master" }.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER DATABASE [{_db.DatabaseName}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE";
            await command.ExecuteNonQueryAsync();
        }
        SqlConnection.ClearAllPools();
        await InsertMessagesAsync(30, "test.noop.v1");
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));

        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Task.Run(() => _h.Processor(clock, o => o.BatchSize = 10).ClaimBatchAsync(CancellationToken.None))));

        var drained = await _h.Processor(clock, o => o.BatchSize = 100).ClaimBatchAsync(CancellationToken.None);
        claims.Append(drained).SelectMany(c => c.Messages.Select(m => m.Id)).Should().OnlyHaveUniqueItems().And.HaveCount(30);
        _output.WriteLine($"RCSI racing claims: {string.Join(", ", claims.Select(c => c.Messages.Count))}; drained afterwards: {drained.Messages.Count}");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>A full game, <paramref name="subscriberCount"/> subscribers, one player leaves: one pending vacancy.</summary>
    private async Task<(Guid OccurrenceId, List<Guid> Subscribers)> VacancyWithSubscribersAsync(int subscriberCount)
    {
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 5);
        var holders = await _h.FillAsync(occurrenceId, requirementId, 5);
        var subscribers = new List<Guid>();
        for (var i = 0; i < subscriberCount; i++)
        {
            var (playerId, token) = await _h.NewPlayerAsync();
            (await _h.SubscribeAsync(token, occurrenceId, requirementId)).StatusCode.Should().Be(HttpStatusCode.Created);
            subscribers.Add(playerId);
        }
        (await _h.LeaveAsync(holders[0].Token, occurrenceId, holders[0].AssignmentId)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _h.OutboxAsync(occurrenceId)).Should().ContainSingle();
        return (occurrenceId, subscribers);
    }

    private async Task<OutboxHandlerResult> HandleDirectlyAsync(OutboxMessage message)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetServices<IOutboxMessageHandler>().Single(h => h.Type == message.Type);
        return await handler.HandleAsync(message, CancellationToken.None);
    }

    private async Task<List<Guid>> InsertMessagesAsync(int count, string type, string payload = "{}")
    {
        var ids = new List<Guid>();
        await using var context = _db.CreateContext();
        var now = DateTime.UtcNow;
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            context.OutboxMessages.Add(new OutboxMessage
            {
                Id = id,
                Type = type,
                AggregateId = Guid.NewGuid(),
                OccurredAtUtc = now,
                PayloadJson = payload,
                DeduplicationKey = $"{type}:{id:N}",
                Status = OutboxMessageStatus.Pending,
                NextAttemptAtUtc = now,
                CreatedAtUtc = now.AddTicks(i)
            });
        }
        await context.SaveChangesAsync();
        return ids;
    }

    private const int OutboxInsertFailure = 50001;

    /// <summary>
    /// A real database-side failure of the outbox INSERT, raised by SQL Server after the
    /// roster UPDATEs of the same batch already executed in the transaction.
    /// </summary>
    private async Task FailEveryOutboxInsertAsync()
    {
        await using var context = _db.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER [TR_OutboxMessages_FailInsert] ON [OutboxMessages] AFTER INSERT " +
            $"AS THROW {OutboxInsertFailure}, N'Simulated outbox insert failure.', 1;");
    }

    /// <summary>Occupies the deduplication key the next vacancy of this assignment would use, so its INSERT fails.</summary>
    private async Task InsertConflictingOutboxRowAsync(Guid assignmentId)
    {
        await using var context = _db.CreateContext();
        var now = DateTime.UtcNow;
        context.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = "test.blocker.v1",
            AggregateId = Guid.Empty,
            OccurredAtUtc = now,
            PayloadJson = "{}",
            DeduplicationKey = RosterVacancyOpenedV1.DeduplicationKeyFor(assignmentId),
            Status = OutboxMessageStatus.Completed,
            NextAttemptAtUtc = now,
            CreatedAtUtc = now
        });
        await context.SaveChangesAsync();
    }

    private async Task<OutboxMessage> MessageAsync(Guid id)
    {
        await using var context = _db.CreateContext();
        return await context.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id);
    }

    private async Task<byte[]> OccurrenceRowVersionAsync(Guid occurrenceId)
    {
        await using var context = _db.CreateContext();
        return await context.Events.AsNoTracking().Where(e => e.Id == occurrenceId).Select(e => e.RowVersion).SingleAsync();
    }
}
