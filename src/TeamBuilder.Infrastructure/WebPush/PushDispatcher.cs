using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Outbox;

namespace TeamBuilder.Infrastructure.WebPush;

/// <summary>
/// Sends queued push deliveries (SQL Server only), one bounded batch per call: the background
/// worker loops it, tests call it directly.
/// <para>
/// Claiming mirrors the outbox: one <c>UPDATE</c> over a <c>TOP (n)</c> CTE read
/// <c>WITH (UPDLOCK, READPAST, ROWLOCK)</c>, so concurrent dispatchers (threads or instances)
/// never hold the same delivery; a lease lets a crashed dispatcher's rows be reclaimed, and
/// every outcome is written only while the caller still owns the lease.
/// </para>
/// <para>
/// Each delivery is decided on its own: abandoned without sending when its subscription is
/// gone, inactive or now belongs to another player, or the alert outlived its TTL; otherwise
/// sent, and then Accepted (2xx), Failed and its subscription disabled (404/410, unusable
/// keys), Failed with the subscription's failure count raised (other 4xx; disabled after
/// <see cref="WebPushOptions.MaxConsecutiveFailures"/>), or retried with backoff (429, 5xx,
/// timeouts, network) until <see cref="WebPushOptions.MaxAttempts"/>. One dead browser fails
/// only its own row.
/// </para>
/// <para>
/// External delivery is at-least-once and best effort: if a dispatcher dies after the push
/// service accepted a message but before recording it, the lease expires and the message is
/// sent again (devices collapse repeats by tag). It is never exactly-once.
/// </para>
/// </summary>
public sealed class PushDispatcher
{
    private static readonly string InstanceId = $"{Environment.MachineName}:{Environment.ProcessId}";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWebPushClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly WebPushOptions _options;
    private readonly ILogger<PushDispatcher> _logger;

    public PushDispatcher(
        IServiceScopeFactory scopeFactory,
        IWebPushClient client,
        TimeProvider timeProvider,
        IOptions<WebPushOptions> options,
        ILogger<PushDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _client = client;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public WebPushOptions Options => _options;

    public async Task<PushBatchResult> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        await FailExhaustedLeasesAsync(cancellationToken);

        var (lockOwner, claimed) = await ClaimAsync(cancellationToken);
        var result = new PushBatchResult { Claimed = claimed.Count };
        if (claimed.Count == 0)
            return result;

        var work = await LoadAsync(claimed, cancellationToken);
        var now = UtcNow();
        var outcomes = new ConcurrentBag<Outcome>();

        var toSend = new List<Work>();
        foreach (var item in work)
        {
            var skip = item switch
            {
                { Endpoint: null } => "SubscriptionGone",
                { SubscriptionActive: false } => "SubscriptionInactive",
                _ when item.SubscriptionPlayerId != item.NotificationPlayerId => "SubscriptionReassigned",
                _ when _options.TimeToLive > TimeSpan.Zero && item.NotificationCreatedAtUtc + _options.TimeToLive <= now => "Expired",
                _ => null
            };
            if (skip is null)
                toSend.Add(item);
            else
                outcomes.Add(new Outcome(item, PushDeliveryStatus.Abandoned, null, skip, null, SubscriptionEffect.None));
        }

        await Parallel.ForEachAsync(
            toSend,
            new ParallelOptions { MaxDegreeOfParallelism = _options.MaxConcurrency, CancellationToken = cancellationToken },
            async (item, token) => outcomes.Add(await SendOneAsync(item, now, token)));

        await RecordAsync(lockOwner, outcomes.ToList(), result);
        return result;
    }

    private async Task<Outcome> SendOneAsync(Work item, DateTime now, CancellationToken cancellationToken)
    {
        var age = now - item.NotificationCreatedAtUtc;
        var ttl = _options.TimeToLive > TimeSpan.Zero
            ? Math.Max(0, (int)(_options.TimeToLive - age).TotalSeconds)
            : 0;
        var payload = PushPayload.For(item.NotificationId, item.OccurrenceId, item.Type, item.Title, item.Body, now);
        var message = new WebPushMessage(payload.ToUtf8Json(), ttl, item.OccurrenceId.ToString("N"));

        var service = PushEndpointPolicy.ServiceLabel(item.Endpoint!);
        RefillTelemetry.PushAttempted.Add(1, new KeyValuePair<string, object?>("service", service));

        WebPushResult sent;
        try
        {
            sent = await _client.SendAsync(new WebPushTarget(item.Endpoint!, item.P256dh!, item.Auth!), message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Only the type: an exception message can carry the endpoint.
            _logger.LogWarning("Push delivery {DeliveryId} to subscription {SubscriptionId} ({Service}) threw {ExceptionType}.",
                item.DeliveryId, item.SubscriptionId, service, ex.GetType().Name);
            sent = new WebPushResult(WebPushOutcome.Transient, null, "ClientError");
        }

        var final = item.AttemptCount >= _options.MaxAttempts;
        return sent.Outcome switch
        {
            WebPushOutcome.Accepted => new Outcome(item, PushDeliveryStatus.Accepted, sent.StatusCode, null, null, SubscriptionEffect.Success, service),
            WebPushOutcome.Gone => new Outcome(item, PushDeliveryStatus.Failed, sent.StatusCode, sent.Category, null, SubscriptionEffect.DisableExpired, service),
            WebPushOutcome.InvalidSubscription => new Outcome(item, PushDeliveryStatus.Failed, sent.StatusCode, sent.Category, null, SubscriptionEffect.DisableRejected, service),
            WebPushOutcome.Rejected => new Outcome(item, PushDeliveryStatus.Failed, sent.StatusCode, sent.Category, null, SubscriptionEffect.Failure, service),
            _ when final => new Outcome(item, PushDeliveryStatus.Failed, sent.StatusCode, $"RetriesExhausted:{sent.Category}", null, SubscriptionEffect.Failure, service),
            _ => new Outcome(item, PushDeliveryStatus.Pending, sent.StatusCode, sent.Category, RetryAt(now, item.AttemptCount, sent.RetryAfter), SubscriptionEffect.None, service)
        };
    }

    private DateTime RetryAt(DateTime now, int attempt, TimeSpan? retryAfter)
    {
        var delay = _options.RetryDelayFor(attempt);
        if (retryAfter is { } wait && wait > delay)
            delay = wait < _options.RetryMaxDelay ? wait : _options.RetryMaxDelay;
        return now + delay;
    }

    /// <summary>Writes every outcome (guarded by the lease), then the subscription feedback, then metrics and logs.</summary>
    private async Task RecordAsync(string lockOwner, List<Outcome> outcomes, PushBatchResult result)
    {
        var now = UtcNow();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

        // Not cancellable: shutdown should not leave sent messages looking unsent until their lease expires.
        var ct = CancellationToken.None;
        foreach (var group in outcomes.GroupBy(o => (o.Status, o.StatusCode, o.Error, o.NextAttemptAtUtc)))
        {
            foreach (var chunk in group.Select(o => o.Work.DeliveryId).Chunk(500))
            {
                var terminal = group.Key.Status != PushDeliveryStatus.Pending;
                var updated = await context.PushDeliveries
                    .Where(d => chunk.Contains(d.Id) && d.Status == PushDeliveryStatus.Sending && d.LockOwner == lockOwner)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.Status, group.Key.Status)
                        .SetProperty(d => d.LastStatusCode, group.Key.StatusCode)
                        .SetProperty(d => d.LastError, group.Key.Error)
                        .SetProperty(d => d.NextAttemptAtUtc, d => group.Key.NextAttemptAtUtc ?? d.NextAttemptAtUtc)
                        .SetProperty(d => d.CompletedAtUtc, terminal ? now : (DateTime?)null)
                        .SetProperty(d => d.LockOwner, (string?)null)
                        .SetProperty(d => d.LockExpiresAtUtc, (DateTime?)null),
                        ct);
                result.LeaseLost += chunk.Length - updated;
            }
        }

        var succeeded = outcomes.Where(o => o.Effect == SubscriptionEffect.Success).Select(o => o.Work.SubscriptionId).Distinct().ToList();
        foreach (var chunk in succeeded.Chunk(500))
        {
            await context.PushSubscriptions
                .Where(s => chunk.Contains(s.Id) && s.FailureCount > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.FailureCount, 0), ct);
        }

        result.SubscriptionsDisabled += await DisableAsync(context, outcomes, SubscriptionEffect.DisableExpired, PushSubscriptionDisabledReasons.Expired, now, ct);
        result.SubscriptionsDisabled += await DisableAsync(context, outcomes, SubscriptionEffect.DisableRejected, PushSubscriptionDisabledReasons.Rejected, now, ct);

        var failing = outcomes.Where(o => o.Effect == SubscriptionEffect.Failure).GroupBy(o => o.Work.SubscriptionId).ToList();
        foreach (var subscription in failing)
        {
            var increment = subscription.Count();
            await context.PushSubscriptions
                .Where(s => s.Id == subscription.Key)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.FailureCount, x => x.FailureCount + increment), ct);
        }
        if (failing.Count > 0)
        {
            var ids = failing.Select(g => g.Key).ToList();
            var max = _options.MaxConsecutiveFailures;
            foreach (var chunk in ids.Chunk(500))
            {
                var disabled = await context.PushSubscriptions
                    .Where(s => chunk.Contains(s.Id) && s.IsActive && s.FailureCount >= max)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.IsActive, false)
                        .SetProperty(x => x.DisabledAtUtc, now)
                        .SetProperty(x => x.DisabledReason, PushSubscriptionDisabledReasons.Rejected), ct);
                if (disabled > 0)
                {
                    result.SubscriptionsDisabled += disabled;
                    RefillTelemetry.PushSubscriptionDisabled.Add(disabled, new KeyValuePair<string, object?>("reason", "TooManyFailures"));
                }
            }
        }

        foreach (var outcome in outcomes)
        {
            var service = new KeyValuePair<string, object?>("service", outcome.Service);
            switch (outcome.Status)
            {
                case PushDeliveryStatus.Accepted:
                    result.Accepted++;
                    RefillTelemetry.PushAccepted.Add(1, service);
                    RefillTelemetry.VacancyToPushAccepted.Record(RefillTelemetry.Milliseconds(outcome.Work.SourceOccurredAtUtc, now));
                    break;
                case PushDeliveryStatus.Abandoned:
                    result.Abandoned++;
                    RefillTelemetry.PushAbandoned.Add(1, new KeyValuePair<string, object?>("reason", outcome.Error));
                    break;
                case PushDeliveryStatus.Failed:
                    result.Failed++;
                    RefillTelemetry.PushPermanentFailure.Add(1, service, new KeyValuePair<string, object?>("category", outcome.Error));
                    _logger.LogWarning(
                        "PushFailed: delivery {DeliveryId} to subscription {SubscriptionId} ({Service}) failed permanently: {Category} (status {StatusCode}, attempt {Attempt}).",
                        outcome.Work.DeliveryId, outcome.Work.SubscriptionId, outcome.Service, outcome.Error, outcome.StatusCode, outcome.Work.AttemptCount);
                    break;
                case PushDeliveryStatus.Pending:
                    result.Retried++;
                    RefillTelemetry.PushTransientFailure.Add(1, service, new KeyValuePair<string, object?>("category", outcome.Error));
                    _logger.LogInformation(
                        "PushRetry: delivery {DeliveryId} to subscription {SubscriptionId} ({Service}) will be retried: {Category} (status {StatusCode}, attempt {Attempt} of {MaxAttempts}).",
                        outcome.Work.DeliveryId, outcome.Work.SubscriptionId, outcome.Service, outcome.Error, outcome.StatusCode, outcome.Work.AttemptCount, _options.MaxAttempts);
                    break;
            }
        }

        if (result.Accepted > 0 || result.Failed > 0 || result.Abandoned > 0)
        {
            _logger.LogInformation(
                "PushBatch: {Claimed} claimed, {Accepted} accepted, {Retried} to retry, {Failed} failed, {Abandoned} abandoned, {Disabled} subscription(s) disabled.",
                result.Claimed, result.Accepted, result.Retried, result.Failed, result.Abandoned, result.SubscriptionsDisabled);
        }
    }

    /// <returns>How many subscriptions this call disabled.</returns>
    private async Task<int> DisableAsync(TeamBuilderDbContext context, List<Outcome> outcomes, SubscriptionEffect effect, string reason, DateTime now, CancellationToken ct)
    {
        var total = 0;
        var ids = outcomes.Where(o => o.Effect == effect).Select(o => o.Work.SubscriptionId).Distinct().ToList();
        foreach (var chunk in ids.Chunk(500))
        {
            var disabled = await context.PushSubscriptions
                .Where(s => chunk.Contains(s.Id) && s.IsActive)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsActive, false)
                    .SetProperty(x => x.DisabledAtUtc, now)
                    .SetProperty(x => x.DisabledReason, reason)
                    .SetProperty(x => x.FailureCount, x => x.FailureCount + 1), ct);
            if (disabled > 0)
            {
                total += disabled;
                RefillTelemetry.PushSubscriptionDisabled.Add(disabled, new KeyValuePair<string, object?>("reason", reason));
                _logger.LogInformation("PushSubscriptionDisabled: {Count} subscription(s) disabled ({Reason}).", disabled, reason);
            }
        }
        return total;
    }

    internal async Task<(string LockOwner, List<PushDelivery> Claimed)> ClaimAsync(CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var leaseUntil = now + _options.LeaseDuration;
        var lockOwner = $"{InstanceId}:{Guid.NewGuid():N}";
        var batchSize = _options.BatchSize;
        var maxAttempts = _options.MaxAttempts;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var claimed = await context.PushDeliveries
            .FromSql($"""
                WITH [batch] AS (
                    SELECT TOP ({batchSize}) *
                    FROM [PushDeliveries] WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE [AttemptCount] < {maxAttempts}
                      AND (([Status] = 1 AND [NextAttemptAtUtc] <= {now})
                        OR ([Status] = 2 AND [LockExpiresAtUtc] <= {now}))
                    ORDER BY [NextAttemptAtUtc], [Id]
                )
                UPDATE [batch]
                SET [Status] = 2,
                    [LockOwner] = {lockOwner},
                    [LockExpiresAtUtc] = {leaseUntil},
                    [AttemptCount] = [AttemptCount] + 1
                OUTPUT INSERTED.*
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return (lockOwner, claimed);
    }

    private async Task<List<Work>> LoadAsync(List<PushDelivery> claimed, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var work = new List<Work>(claimed.Count);
        foreach (var chunk in claimed.Chunk(500))
        {
            var ids = chunk.Select(d => d.Id).ToList();
            var rows = await (
                    from d in context.PushDeliveries.AsNoTracking()
                    where ids.Contains(d.Id)
                    join n in context.InAppNotifications.AsNoTracking() on d.InAppNotificationId equals n.Id
                    join s in context.PushSubscriptions.AsNoTracking() on d.PushSubscriptionId equals s.Id into subs
                    from s in subs.DefaultIfEmpty()
                    select new Work
                    {
                        DeliveryId = d.Id,
                        SubscriptionId = d.PushSubscriptionId,
                        SourceOccurredAtUtc = d.SourceOccurredAtUtc,
                        NotificationId = n.Id,
                        NotificationPlayerId = n.PlayerId,
                        NotificationCreatedAtUtc = n.CreatedAtUtc,
                        OccurrenceId = n.OccurrenceId,
                        Type = n.Type,
                        Title = n.Title,
                        Body = n.Body,
                        SubscriptionPlayerId = s == null ? null : s.PlayerId,
                        SubscriptionActive = s != null && s.IsActive,
                        Endpoint = s == null ? null : s.Endpoint,
                        P256dh = s == null ? null : s.P256dh,
                        Auth = s == null ? null : s.Auth
                    })
                .ToListAsync(cancellationToken);
            var attempts = chunk.ToDictionary(d => d.Id, d => d.AttemptCount);
            foreach (var row in rows)
                row.AttemptCount = attempts[row.DeliveryId];
            work.AddRange(rows);
        }
        return work;
    }

    /// <summary>A Sending delivery whose last allowed attempt's lease expired is never claimable again: Failed.</summary>
    private async Task FailExhaustedLeasesAsync(CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var maxAttempts = _options.MaxAttempts;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var failed = await context.PushDeliveries
            .Where(d => d.Status == PushDeliveryStatus.Sending && d.LockExpiresAtUtc <= now && d.AttemptCount >= maxAttempts)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, PushDeliveryStatus.Failed)
                .SetProperty(d => d.CompletedAtUtc, now)
                .SetProperty(d => d.LockOwner, (string?)null)
                .SetProperty(d => d.LockExpiresAtUtc, (DateTime?)null)
                .SetProperty(d => d.LastError, "LeaseExpired"),
                cancellationToken);
        if (failed > 0)
        {
            RefillTelemetry.PushPermanentFailure.Add(failed, new KeyValuePair<string, object?>("category", "LeaseExpired"));
            _logger.LogWarning("PushFailed: {Count} delivery(ies) gave up after the lease of their final attempt expired.", failed);
        }
    }

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private enum SubscriptionEffect
    {
        None,
        Success,
        Failure,
        DisableExpired,
        DisableRejected
    }

    private sealed record Outcome(
        Work Work,
        PushDeliveryStatus Status,
        int? StatusCode,
        string? Error,
        DateTime? NextAttemptAtUtc,
        SubscriptionEffect Effect,
        string Service = "none");

    private sealed class Work
    {
        public Guid DeliveryId { get; init; }
        public Guid SubscriptionId { get; init; }
        public DateTime SourceOccurredAtUtc { get; init; }
        public int AttemptCount { get; set; }
        public Guid NotificationId { get; init; }
        public Guid NotificationPlayerId { get; init; }
        public DateTime NotificationCreatedAtUtc { get; init; }
        public Guid OccurrenceId { get; init; }
        public string Type { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Body { get; init; } = string.Empty;
        public Guid? SubscriptionPlayerId { get; init; }
        public bool SubscriptionActive { get; init; }
        public string? Endpoint { get; init; }
        public string? P256dh { get; init; }
        public string? Auth { get; init; }
    }
}

/// <summary>Counts of one <see cref="PushDispatcher.ProcessBatchAsync"/> call.</summary>
public sealed class PushBatchResult
{
    public int Claimed { get; set; }
    public int Accepted { get; set; }
    public int Retried { get; set; }
    public int Failed { get; set; }
    public int Abandoned { get; set; }
    public int SubscriptionsDisabled { get; set; }

    /// <summary>Outcomes not recorded because another dispatcher reclaimed the delivery first.</summary>
    public int LeaseLost { get; set; }
}
