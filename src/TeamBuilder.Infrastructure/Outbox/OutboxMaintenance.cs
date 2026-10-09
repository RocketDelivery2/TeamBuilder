using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Infrastructure.Outbox;

/// <summary>
/// Retention for the refill pipeline (SQL Server only). Each table is purged in bounded
/// batches: one <c>DELETE TOP (n) … WITH (READPAST, ROWLOCK)</c> per batch, each its own
/// autocommit transaction, so a pass never holds a large lock or a long transaction, and
/// several instances purging at once simply skip each other's locked rows. The predicates are
/// served by filtered indexes on the terminal statuses, so the purge never scans live rows.
/// <list type="bullet">
/// <item>OutboxMessages: Completed with ProcessedAtUtc before the cutoff (and Failed, only
/// with an explicit <see cref="OutboxMaintenanceOptions.FailedRetention"/>). Pending and
/// Processing are never deleted. A purged message's deduplication key is gone too, which is
/// safe: the key is per ended assignment, and an assignment ends only once.</item>
/// <item>PushDeliveries: Accepted, Failed or Abandoned before the cutoff.</item>
/// <item>PushSubscriptions: disabled before the cutoff (their credentials are dead).</item>
/// </list>
/// </summary>
public sealed class OutboxMaintenance
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly OutboxMaintenanceOptions _options;
    private readonly ILogger<OutboxMaintenance> _logger;

    public OutboxMaintenance(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<OutboxMaintenanceOptions> options,
        ILogger<OutboxMaintenance> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public OutboxMaintenanceOptions Options => _options;

    public async Task<OutboxPurgeResult> PurgeAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var result = new OutboxPurgeResult
        {
            CompletedMessages = await PurgeTableAsync(
                "OutboxMessages",
                $"[Status] = 3 AND [ProcessedAtUtc] < @cutoff",
                "IX_OutboxMessages_Completed_ProcessedAtUtc",
                now - _options.CompletedRetention,
                cancellationToken),
            PushDeliveries = await PurgeTableAsync(
                "PushDeliveries",
                "[Status] >= 3 AND [CompletedAtUtc] < @cutoff",
                "IX_PushDeliveries_Terminal_CompletedAtUtc",
                now - _options.PushDeliveryRetention,
                cancellationToken),
            DisabledPushSubscriptions = await PurgeTableAsync(
                "PushSubscriptions",
                "[IsActive] = 0 AND [DisabledAtUtc] < @cutoff",
                null,
                now - _options.DisabledPushSubscriptionRetention,
                cancellationToken)
        };

        if (_options.FailedRetention is { } failedRetention)
        {
            result.FailedMessages = await PurgeTableAsync(
                "OutboxMessages",
                "[Status] = 4 AND [ProcessedAtUtc] < @cutoff",
                "IX_OutboxMessages_Failed_ProcessedAtUtc",
                now - failedRetention,
                cancellationToken);
        }

        if (result.Total > 0)
        {
            _logger.LogInformation(
                "OutboxPurged: {Completed} completed message(s), {Failed} failed message(s), {Deliveries} push delivery(ies), {Subscriptions} disabled push subscription(s).",
                result.CompletedMessages, result.FailedMessages, result.PushDeliveries, result.DisabledPushSubscriptions);
        }

        return result;
    }

    /// <summary>Deletes matching rows in batches until fewer than a full batch remain or the per-run cap is hit.</summary>
    private async Task<int> PurgeTableAsync(string table, string predicate, string? index, DateTime cutoff, CancellationToken cancellationToken)
    {
        var total = 0;
        var hint = index is null ? "READPAST, ROWLOCK" : $"READPAST, ROWLOCK, INDEX([{index}])";
        // Table hints (and the index hint) are legal only in the FROM clause, hence the alias form.
        var sql = $"DELETE TOP (@batch) t FROM [{table}] AS t WITH ({hint}) WHERE {predicate};";

        for (var batch = 0; batch < _options.MaxBatchesPerRun; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var scope = _scopeFactory.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
            var deleted = await context.Database.ExecuteSqlRawAsync(
                sql,
                [
                    new Microsoft.Data.SqlClient.SqlParameter("@batch", _options.BatchSize),
                    new Microsoft.Data.SqlClient.SqlParameter("@cutoff", System.Data.SqlDbType.DateTime2) { Value = cutoff }
                ],
                cancellationToken);

            total += deleted;
            if (deleted > 0)
                RefillTelemetry.OutboxPurged.Add(deleted, new KeyValuePair<string, object?>("table", table));
            if (deleted < _options.BatchSize)
                break;
        }

        return total;
    }
}

public sealed class OutboxPurgeResult
{
    public int CompletedMessages { get; set; }
    public int FailedMessages { get; set; }
    public int PushDeliveries { get; set; }
    public int DisabledPushSubscriptions { get; set; }
    public int Total => CompletedMessages + FailedMessages + PushDeliveries + DisabledPushSubscriptions;
}
