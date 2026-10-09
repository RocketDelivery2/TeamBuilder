using System.Diagnostics;
using System.Net;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamBuilder.Api.Operations;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Outbox;
using TeamBuilder.Tests.Integration;
using TeamBuilder.Tests.Support;
using Xunit.Abstractions;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Outbox retention and operator replay on a real SQL Server 2022 database, at realistic
/// volume: the purge deletes only terminal history older than its cutoff, in bounded batches,
/// with several purgers at once; Pending, Processing and Failed rows survive unless a Failed
/// retention is configured; replay requeues one Failed message once, keeps its attempt
/// history, and the idempotent handler never duplicates notifications; the CLI shows metadata,
/// never payloads.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class OutboxOperationsSqlServerIntegrationTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = Now.AddDays(-8);
    private static readonly DateTime Recent = Now.AddDays(-6);

    private readonly SqlServerContainerFixture _fixture;
    private readonly ITestOutputHelper _output;
    private SqlServerTestDatabase _db = null!;
    private SqlServerWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private RefillHarness _h = null!;

    public OutboxOperationsSqlServerIntegrationTests(SqlServerContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "outboxops");
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

    // ── retention ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Purge_At50kRows_DeletesOnlyOldCompleted_InBoundedBatches_AndNeverLiveOrFailedRows()
    {
        await InsertMessagesAsync(50_000, OutboxMessageStatus.Completed, Old);
        await InsertMessagesAsync(5_000, OutboxMessageStatus.Completed, Recent);
        await InsertMessagesAsync(2_000, OutboxMessageStatus.Pending, null, createdAt: Old);
        await InsertMessagesAsync(1_000, OutboxMessageStatus.Processing, null, createdAt: Old);
        await InsertMessagesAsync(1_000, OutboxMessageStatus.Failed, Old.AddDays(-30));
        var deletes = await CaptureDeleteRowCountsAsync(async () =>
        {
            var timer = Stopwatch.StartNew();
            var result = await Maintenance().PurgeAsync();
            _output.WriteLine($"purged {result.CompletedMessages} rows in {timer.ElapsedMilliseconds} ms");
            result.CompletedMessages.Should().Be(50_000);
            result.FailedMessages.Should().Be(0);
        });

        deletes.Should().OnlyContain(n => n <= 1_000, "every DELETE is one bounded batch");
        (await CountsAsync()).Should().Be((Pending: 2_000, Processing: 1_000, Completed: 5_000, Failed: 1_000));
        (await Maintenance().PurgeAsync()).Total.Should().Be(0, "a second pass finds nothing");
    }

    [Fact]
    public async Task Purge_StopsAtTheBatchCap_AndTheNextPassContinues()
    {
        await InsertMessagesAsync(5_000, OutboxMessageStatus.Completed, Old);
        var capped = Maintenance(o => { o.BatchSize = 500; o.MaxBatchesPerRun = 3; });

        (await capped.PurgeAsync()).CompletedMessages.Should().Be(1_500);
        (await CountsAsync()).Completed.Should().Be(3_500);
        (await capped.PurgeAsync()).CompletedMessages.Should().Be(1_500);
    }

    [Fact]
    public async Task ConcurrentPurgers_ShareTheWork_WithoutErrorsOrDoubleCounting()
    {
        await InsertMessagesAsync(30_000, OutboxMessageStatus.Completed, Old);
        await InsertMessagesAsync(500, OutboxMessageStatus.Pending, null, createdAt: Old);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Maintenance(o => o.BatchSize = 250).PurgeAsync())));

        results.Sum(r => r.CompletedMessages).Should().Be(30_000);
        (await CountsAsync()).Should().Be((Pending: 500, Processing: 0, Completed: 0, Failed: 0));
    }

    [Fact]
    public async Task Purge_WhileTheWorkerIsDelivering_LeavesTheLivePipelineAlone()
    {
        await InsertMessagesAsync(20_000, OutboxMessageStatus.Completed, Old);
        var (occurrenceId, subscribers) = await VacancyWithSubscribersAsync(25);

        var purge = Task.Run(() => Maintenance(o => o.BatchSize = 200).PurgeAsync());
        var processed = await _h.Processor().ProcessBatchAsync();
        await purge;

        processed.Processed.Should().Be(1);
        (await _h.NotificationRowsAsync(occurrenceId)).Should().HaveCount(subscribers);
        (await _h.OutboxAsync(occurrenceId)).Single().Status.Should().Be(OutboxMessageStatus.Completed, "it completed just now, inside retention");
        (await CountsAsync()).Completed.Should().Be(1);
    }

    [Fact]
    public async Task FailedRetention_IsOptIn_AndThenDeletesOnlyOldFailures()
    {
        await InsertMessagesAsync(300, OutboxMessageStatus.Failed, Now.AddDays(-40));
        await InsertMessagesAsync(200, OutboxMessageStatus.Failed, Now.AddDays(-10));

        (await Maintenance().PurgeAsync()).FailedMessages.Should().Be(0);
        (await CountsAsync()).Failed.Should().Be(500);

        (await Maintenance(o => o.FailedRetention = TimeSpan.FromDays(30)).PurgeAsync()).FailedMessages.Should().Be(300);
        (await CountsAsync()).Failed.Should().Be(200);
    }

    [Fact]
    public async Task Purge_RemovesFinishedPushDeliveries_AndOldDisabledSubscriptions_Only()
    {
        var (occurrenceId, _) = await VacancyWithSubscribersAsync(1);
        await _h.Processor().ProcessBatchAsync();
        var notificationId = (await _h.NotificationRowsAsync(occurrenceId)).Single().Id;
        var playerId = (await _h.NotificationRowsAsync(occurrenceId)).Single().PlayerId;
        await ExecuteAsync($"""
            INSERT INTO [PushDeliveries] ([Id], [InAppNotificationId], [PushSubscriptionId], [Status], [AttemptCount], [NextAttemptAtUtc], [SourceOccurredAtUtc], [CreatedAtUtc], [CompletedAtUtc])
            SELECT NEWID(), '{notificationId}', NEWID(), s.[Status], 1, @old, @old, @old, CASE WHEN s.[Status] >= 3 THEN @completed END
            FROM (SELECT TOP (4000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) x
            CROSS APPLY (SELECT (x.n % 5) + 1 AS [Status]) s;
            INSERT INTO [PushDeliveries] ([Id], [InAppNotificationId], [PushSubscriptionId], [Status], [AttemptCount], [NextAttemptAtUtc], [SourceOccurredAtUtc], [CreatedAtUtc], [CompletedAtUtc])
            SELECT TOP (100) NEWID(), '{notificationId}', NEWID(), 3, 1, @old, @old, @old, @recent FROM sys.all_objects;
            INSERT INTO [PushSubscriptions] ([Id], [PlayerId], [Endpoint], [EndpointHash], [P256dh], [Auth], [LastSeenAtUtc], [IsActive], [FailureCount], [DisabledAtUtc], [DisabledReason], [CreatedAtUtc])
            SELECT TOP (300) NEWID(), '{playerId}', CONCAT('https://fcm.googleapis.com/fcm/send/', NEWID()), CRYPT_GEN_RANDOM(32), 'k', 'a', @old,
                   CASE WHEN ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 3 = 0 THEN 1 ELSE 0 END, 0,
                   CASE WHEN ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 3 = 0 THEN NULL
                        WHEN ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 3 = 1 THEN @veryOld ELSE @old END,
                   CASE WHEN ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 3 = 0 THEN NULL ELSE 'Expired' END, @veryOld
            FROM sys.all_objects;
            """, ("@old", Old), ("@completed", Old), ("@recent", Recent), ("@veryOld", Now.AddDays(-40)));

        var result = await Maintenance().PurgeAsync();

        result.PushDeliveries.Should().Be(2400, "Accepted, Failed and Abandoned (3/5 of 4000) older than 7 days");
        result.DisabledPushSubscriptions.Should().Be(100, "only those disabled more than 30 days ago");
        (await ScalarAsync<int>("SELECT COUNT(*) FROM [PushDeliveries] WHERE [Status] IN (1, 2)")).Should().Be(1600);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM [PushDeliveries] WHERE [Status] >= 3")).Should().Be(100);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM [PushSubscriptions] WHERE [IsActive] = 1")).Should().Be(100);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM [PushSubscriptions] WHERE [IsActive] = 0")).Should().Be(100);
    }

    // ── replay ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Replay_RequeuesAFailedVacancyOnce_KeepsItsHistory_AndDeliversExactlyOneNotificationEach()
    {
        var (occurrenceId, subscribers) = await VacancyWithSubscribersAsync(3);
        var messageId = (await _h.OutboxAsync(occurrenceId)).Single().Id;
        await MarkFailedAsync(messageId, attempts: 8, error: new string('x', 2000));

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<OutboxOperations>();
            var failed = (await operations.ListFailedAsync()).Should().ContainSingle().Subject;
            (failed.Id, failed.AttemptCount, failed.LastError!.Length).Should().Be((messageId, 8, OutboxOperations.ErrorPreviewLength + 1));

            var both = await Task.WhenAll(operations.ReplayAsync(messageId), ReplayInOwnScopeAsync(messageId));
            both.Should().BeEquivalentTo([OutboxReplayResult.Requeued, OutboxReplayResult.NotFailed], "racing operators requeue it once");
            (await operations.ReplayAsync(Guid.NewGuid())).Should().Be(OutboxReplayResult.NotFound);
        }

        var requeued = (await _h.OutboxAsync(occurrenceId)).Single();
        (requeued.Status, requeued.AttemptCount, requeued.PriorAttemptCount, requeued.ReplayCount).Should().Be((OutboxMessageStatus.Pending, 0, 8, 1));
        requeued.LastReplayedAtUtc.Should().NotBeNull();

        await _h.Processor().ProcessBatchAsync();
        (await _h.NotificationRowsAsync(occurrenceId)).Should().HaveCount(subscribers);

        // Replaying an already-delivered message (an operator mistake) adds nothing.
        await MarkFailedAsync(messageId, attempts: 1, error: "boom");
        await using (var scope = _factory.Services.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<OutboxOperations>().ReplayAsync(messageId)).Should().Be(OutboxReplayResult.Requeued);
        await _h.Processor().ProcessBatchAsync();
        (await _h.NotificationRowsAsync(occurrenceId)).Should().HaveCount(subscribers);
        var twice = (await _h.OutboxAsync(occurrenceId)).Single();
        (twice.Status, twice.ReplayCount, twice.PriorAttemptCount).Should().Be((OutboxMessageStatus.Completed, 2, 9));
    }

    [Fact]
    public async Task Cli_ShowsFailedMetadataWithoutPayloads_ReplaysAndPurges()
    {
        var secretId = Guid.NewGuid();
        await ExecuteAsync($$"""
            INSERT INTO [OutboxMessages] ([Id], [Type], [AggregateId], [OccurredAtUtc], [PayloadJson], [DeduplicationKey], [Status], [AttemptCount], [NextAttemptAtUtc], [ProcessedAtUtc], [LastError], [CreatedAtUtc], [ReplayCount], [PriorAttemptCount])
            VALUES ('{{secretId}}', 'roster.vacancy.opened.v1', NEWID(), @old, N'{"venueAddress":"221B Secret Street","lat":40.1}', 'cli-k', 4, 5, @old, @old, N'Handler threw TimeoutException', @old, 0, 0);
            """, ("@old", Old));
        await InsertMessagesAsync(10, OutboxMessageStatus.Completed, Old);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TeamBuilderSql"] = _db.ConnectionString })
            .Build();

        async Task<(int Code, string Output)> Run(params string[] args)
        {
            var output = new StringWriter();
            var code = await OutboxCommand.RunAsync(args, output, configuration);
            return (code, output.ToString());
        }

        var status = await Run("status");
        status.Should().Be((0, $"Pending 0  Processing 0  Completed 10  Failed 1{Environment.NewLine}"));
        var failed = await Run("failed");
        failed.Output.Should().Contain(secretId.ToString()).And.Contain("roster.vacancy.opened.v1").And.Contain("attempts 5").And.Contain("TimeoutException");
        failed.Output.Should().NotContain("Secret Street").And.NotContain("40.1");
        (await Run("replay", secretId.ToString())).Should().Be((0, $"Requeued {secretId}; a worker will process it on its next poll.{Environment.NewLine}"));
        (await Run("replay", secretId.ToString())).Code.Should().Be(0);
        (await Run("replay", Guid.NewGuid().ToString())).Code.Should().Be(1);
        (await Run("replay")).Code.Should().Be(2);
        (await Run("purge")).Output.Should().StartWith("Purged 10 completed and 0 failed message(s)");
        (await Run("bogus")).Code.Should().Be(2);
        var keys = await Run("vapid-keys");
        keys.Output.Should().MatchRegex(@"^WebPush__VapidPublicKey=[A-Za-z0-9_-]{87}\r?\nWebPush__VapidPrivateKey=[A-Za-z0-9_-]{43}\r?\n$");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private OutboxMaintenance Maintenance(Action<OutboxMaintenanceOptions>? configure = null)
    {
        var options = new OutboxMaintenanceOptions();
        configure?.Invoke(options);
        return new OutboxMaintenance(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new FixedTimeProvider(new DateTimeOffset(Now)),
            Options.Create(options),
            NullLogger<OutboxMaintenance>.Instance);
    }

    private async Task<OutboxReplayResult> ReplayInOwnScopeAsync(Guid id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OutboxOperations>().ReplayAsync(id);
    }

    private async Task<(Guid OccurrenceId, int Subscribers)> VacancyWithSubscribersAsync(int subscribers)
    {
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 2);
        var holders = await _h.FillAsync(occurrenceId, requirementId, 2);
        for (var i = 0; i < subscribers; i++)
        {
            var (_, token) = await _h.NewPlayerAsync();
            (await _h.SubscribeAsync(token, occurrenceId, requirementId)).StatusCode.Should().Be(HttpStatusCode.Created);
        }
        (await _h.LeaveAsync(holders[0].Token, occurrenceId, holders[0].AssignmentId)).StatusCode.Should().Be(HttpStatusCode.OK);
        return (occurrenceId, subscribers);
    }

    private Task MarkFailedAsync(Guid id, int attempts, string error) => ExecuteAsync(
        "UPDATE [OutboxMessages] SET [Status] = 4, [AttemptCount] = @attempts, [LastError] = @error, [ProcessedAtUtc] = SYSUTCDATETIME() WHERE [Id] = @id;",
        ("@attempts", attempts), ("@error", error), ("@id", id));

    /// <summary>Set-based insert of synthetic messages (the realistic-volume seed; EF row-by-row would dominate the test).</summary>
    private Task InsertMessagesAsync(int count, OutboxMessageStatus status, DateTime? processedAt, DateTime? createdAt = null) => ExecuteAsync(
        """
        INSERT INTO [OutboxMessages] ([Id], [Type], [AggregateId], [OccurredAtUtc], [PayloadJson], [DeduplicationKey], [Status], [AttemptCount], [NextAttemptAtUtc], [LockOwner], [LockExpiresAtUtc], [ProcessedAtUtc], [CreatedAtUtc], [ReplayCount], [PriorAttemptCount])
        SELECT NEWID(), 'test.retention.v1', NEWID(), @created, N'{}', CONCAT('test:', CONVERT(varchar(36), NEWID())), @status, 1,
               @created, CASE WHEN @status = 2 THEN 'worker-x' END, CASE WHEN @status = 2 THEN DATEADD(minute, 1, @created) END,
               @processed, @created, 0, 0
        FROM (SELECT TOP (@count) 1 AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) x;
        """,
        ("@count", count), ("@status", (int)status), ("@processed", (object?)processedAt ?? DBNull.Value), ("@created", createdAt ?? processedAt ?? Now));

    private async Task<(int Pending, int Processing, int Completed, int Failed)> CountsAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var counts = await scope.ServiceProvider.GetRequiredService<OutboxOperations>().CountAsync();
        return (counts.Pending, counts.Processing, counts.Completed, counts.Failed);
    }

    /// <summary>Row counts of every DELETE the purge issued, recorded by a temporary AFTER DELETE trigger (NOCOUNT, so it never inflates the purge's own row counts).</summary>
    private async Task<List<int>> CaptureDeleteRowCountsAsync(Func<Task> act)
    {
        await ExecuteAsync("""
            CREATE TABLE [__DeleteBatches] ([Rows] int NOT NULL);
            EXEC (N'CREATE TRIGGER [TR_OutboxMessages_CountDeletes] ON [OutboxMessages] AFTER DELETE AS SET NOCOUNT ON; INSERT INTO [__DeleteBatches] ([Rows]) SELECT COUNT(*) FROM deleted;');
            """);
        try
        {
            await act();
            await using var connection = new SqlConnection(_db.ConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand("SELECT [Rows] FROM [__DeleteBatches];", connection);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<int>();
            while (await reader.ReadAsync()) rows.Add(reader.GetInt32(0));
            _output.WriteLine($"{rows.Count} DELETE statements, largest {rows.DefaultIfEmpty().Max()} rows");
            return rows;
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER [TR_OutboxMessages_CountDeletes]; DROP TABLE [__DeleteBatches];");
        }
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
