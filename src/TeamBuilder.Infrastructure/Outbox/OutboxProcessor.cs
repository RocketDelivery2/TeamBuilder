using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Infrastructure.Outbox;

/// <summary>
/// Claims and processes one bounded batch of outbox messages (SQL Server only). The background
/// worker calls <see cref="ProcessBatchAsync"/> in a loop; tests call it directly, so nothing
/// depends on sleeps.
/// <para>
/// Claiming is one UPDATE over a <c>TOP (n)</c> CTE read <c>WITH (UPDLOCK, READPAST, ROWLOCK)</c>:
/// concurrent workers (other threads or application instances) skip each other's locked rows
/// and can never claim the same message under the same lease. A claim sets Processing, a new
/// lease (<see cref="OutboxOptions.LeaseDuration"/>), a claim token unique to the batch, and
/// increments AttemptCount. A message whose lease expired (its worker crashed or stalled) is
/// claimable again; completion and failure updates require the caller's claim token, so a
/// worker that lost its lease cannot overwrite the new owner's outcome.
/// </para>
/// <para>
/// This is at-least-once processing: if a worker dies after its handler committed but before
/// completion, the message is processed again and the handler's idempotency (for vacancies,
/// the unique (SourceEventId, PlayerId) notification key) absorbs the duplicate.
/// </para>
/// </summary>
public sealed class OutboxProcessor
{
    /// <summary>Process-wide prefix of claim tokens, for diagnosing which instance holds a lease.</summary>
    private static readonly string InstanceId = $"{Environment.MachineName}:{Environment.ProcessId}";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxProcessor> _logger;

    public OutboxProcessor(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<OutboxOptions> options,
        ILogger<OutboxProcessor> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public OutboxOptions Options => _options;

    /// <summary>
    /// Fails out messages whose final lease expired, claims up to BatchSize due messages, and
    /// processes them one by one, each in its own DI scope. Returns what happened.
    /// </summary>
    public async Task<OutboxBatchResult> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        await FailExhaustedLeasesAsync(cancellationToken);

        var claim = await ClaimBatchAsync(cancellationToken);
        var result = new OutboxBatchResult { Claimed = claim.Messages.Count };

        foreach (var message in claim.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessOneAsync(message, claim.LockOwner, result, cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// Claims up to BatchSize due messages under one new claim token: Pending ones whose
    /// NextAttemptAtUtc has come, and Processing ones whose lease expired, oldest first, and only
    /// while they have attempts left. Exposed for the concurrency tests (two claimers, a worker
    /// that "crashes" after claiming).
    /// </summary>
    internal async Task<OutboxClaim> ClaimBatchAsync(CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var leaseUntil = now + _options.LeaseDuration;
        var lockOwner = $"{InstanceId}:{Guid.NewGuid():N}";
        var batchSize = _options.BatchSize;
        var maxAttempts = _options.MaxAttempts;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

        // Statuses are literals so the predicates match the filtered indexes.
        var messages = await context.OutboxMessages
            .FromSql($"""
                WITH [batch] AS (
                    SELECT TOP ({batchSize}) *
                    FROM [OutboxMessages] WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE [AttemptCount] < {maxAttempts}
                      AND (([Status] = 1 AND [NextAttemptAtUtc] <= {now})
                        OR ([Status] = 2 AND [LockExpiresAtUtc] <= {now}))
                    ORDER BY [CreatedAtUtc], [Id]
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

        return new OutboxClaim(lockOwner, messages.OrderBy(m => m.CreatedAtUtc).ThenBy(m => m.Id).ToList());
    }

    private async Task ProcessOneAsync(OutboxMessage message, string lockOwner, OutboxBatchResult result, CancellationToken cancellationToken)
    {
        OutboxHandlerResult outcome;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetServices<IOutboxMessageHandler>()
                .FirstOrDefault(h => string.Equals(h.Type, message.Type, StringComparison.Ordinal));
            if (handler is null)
            {
                // Not retryable: no deployment of this code will ever handle it.
                await FailAsync(message, lockOwner, $"No handler for outbox message type '{message.Type}'.", final: true);
                result.Failed++;
                return;
            }

            outcome = await handler.HandleAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var final = message.AttemptCount >= _options.MaxAttempts;
            await FailAsync(message, lockOwner, Describe(ex), final);
            result.Failed++;
            _logger.LogWarning(
                ex,
                "OutboxFailed: message {MessageId} ({Type}) attempt {Attempt} of {MaxAttempts} failed{Final}.",
                message.Id, message.Type, message.AttemptCount, _options.MaxAttempts, final ? "; giving up" : "; will retry");
            return;
        }

        if (!await CompleteAsync(message, lockOwner, cancellationToken))
        {
            // The lease expired and another worker reclaimed the message; it will complete it.
            result.LeaseLost++;
            _logger.LogWarning("Outbox message {MessageId} ({Type}) was handled after its lease was lost; the new owner completes it.", message.Id, message.Type);
            return;
        }

        if (outcome.Effects > 0)
        {
            result.Processed++;
            RefillTelemetry.OutboxProcessed.Add(1, new KeyValuePair<string, object?>("type", message.Type));
            _logger.LogInformation(
                "OutboxProcessed: message {MessageId} ({Type}) {Outcome} with {Effects} effect(s) on attempt {Attempt}.",
                message.Id, message.Type, outcome.Outcome, outcome.Effects, message.AttemptCount);
        }
        else
        {
            result.Skipped++;
            RefillTelemetry.OutboxSkipped.Add(1, new KeyValuePair<string, object?>("type", message.Type), new KeyValuePair<string, object?>("outcome", outcome.Outcome));
            _logger.LogInformation(
                "OutboxSkipped: message {MessageId} ({Type}) completed without effects: {Outcome}.",
                message.Id, message.Type, outcome.Outcome);
        }
    }

    private async Task<bool> CompleteAsync(OutboxMessage message, string lockOwner, CancellationToken cancellationToken)
    {
        var now = UtcNow();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var updated = await context.OutboxMessages
            .Where(m => m.Id == message.Id && m.Status == OutboxMessageStatus.Processing && m.LockOwner == lockOwner)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxMessageStatus.Completed)
                .SetProperty(m => m.ProcessedAtUtc, now)
                .SetProperty(m => m.LockOwner, (string?)null)
                .SetProperty(m => m.LockExpiresAtUtc, (DateTime?)null)
                .SetProperty(m => m.LastError, (string?)null),
                cancellationToken);
        return updated == 1;
    }

    /// <summary>
    /// Records a failed attempt: back to Pending with a backoff, or Failed after the last
    /// attempt. Not cancellable, so shutdown does not leave the message Processing until its
    /// lease expires.
    /// </summary>
    private async Task FailAsync(OutboxMessage message, string lockOwner, string error, bool final)
    {
        var now = UtcNow();
        var nextAttempt = now + _options.RetryDelayFor(message.AttemptCount);
        var status = final ? OutboxMessageStatus.Failed : OutboxMessageStatus.Pending;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
            await context.OutboxMessages
                .Where(m => m.Id == message.Id && m.Status == OutboxMessageStatus.Processing && m.LockOwner == lockOwner)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, status)
                    .SetProperty(m => m.NextAttemptAtUtc, nextAttempt)
                    .SetProperty(m => m.ProcessedAtUtc, final ? now : (DateTime?)null)
                    .SetProperty(m => m.LockOwner, (string?)null)
                    .SetProperty(m => m.LockExpiresAtUtc, (DateTime?)null)
                    .SetProperty(m => m.LastError, error),
                    CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The lease still expires, so the message is retried anyway.
            _logger.LogError(ex, "Recording the failure of outbox message {MessageId} failed.", message.Id);
        }

        RefillTelemetry.OutboxFailed.Add(1, new KeyValuePair<string, object?>("type", message.Type), new KeyValuePair<string, object?>("final", final));
    }

    /// <summary>A Processing message whose last allowed attempt's lease expired is never claimable again: mark it Failed.</summary>
    private async Task FailExhaustedLeasesAsync(CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var maxAttempts = _options.MaxAttempts;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var failed = await context.OutboxMessages
            .Where(m => m.Status == OutboxMessageStatus.Processing && m.LockExpiresAtUtc <= now && m.AttemptCount >= maxAttempts)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxMessageStatus.Failed)
                .SetProperty(m => m.ProcessedAtUtc, now)
                .SetProperty(m => m.LockOwner, (string?)null)
                .SetProperty(m => m.LockExpiresAtUtc, (DateTime?)null)
                .SetProperty(m => m.LastError, "The lease of the final attempt expired before the message was completed."),
                cancellationToken);
        if (failed > 0)
        {
            RefillTelemetry.OutboxFailed.Add(failed, new KeyValuePair<string, object?>("final", true));
            _logger.LogWarning("OutboxFailed: {Count} message(s) gave up after the lease of their final attempt expired.", failed);
        }
    }

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>Exception type and message only, truncated: no stack, no request data.</summary>
    private static string Describe(Exception ex)
    {
        var text = $"{ex.GetType().Name}: {ex.Message}";
        return text.Length <= Data.Configurations.OutboxMessageConfiguration.LastErrorMaxLength
            ? text
            : text[..Data.Configurations.OutboxMessageConfiguration.LastErrorMaxLength];
    }
}

internal sealed record OutboxClaim(string LockOwner, IReadOnlyList<OutboxMessage> Messages);

/// <summary>Counts of one <see cref="OutboxProcessor.ProcessBatchAsync"/> call.</summary>
public sealed class OutboxBatchResult
{
    public int Claimed { get; set; }

    /// <summary>Completed with at least one side effect.</summary>
    public int Processed { get; set; }

    /// <summary>Completed with nothing to do (revalidation suppressed it).</summary>
    public int Skipped { get; set; }

    /// <summary>Attempts that threw (retried later or Failed).</summary>
    public int Failed { get; set; }

    /// <summary>Handled after the lease was lost to another worker; not completed by this one.</summary>
    public int LeaseLost { get; set; }
}
