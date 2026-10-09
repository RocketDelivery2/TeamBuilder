using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Infrastructure.Outbox;

/// <summary>
/// Operator tools for Failed outbox messages, exposed only through the <c>outbox</c>
/// maintenance command of the API binary (there is no HTTP route and no admin role): whoever
/// can run it already holds the database connection string.
/// <para>
/// Inspection returns metadata only: never the payload. Replay requeues one Failed message:
/// its attempts move to <see cref="Domain.Entities.OutboxMessage.PriorAttemptCount"/>,
/// <see cref="Domain.Entities.OutboxMessage.ReplayCount"/> goes up, and it becomes Pending
/// and due now. The update is conditional on Status = Failed, so replaying twice (or racing
/// another operator) requeues it once. Handlers are idempotent, so a replay never duplicates
/// a notification that an earlier attempt already created.
/// </para>
/// </summary>
public sealed class OutboxOperations
{
    /// <summary>Longest LastError shown; the column holds exception text, which is not for broad display.</summary>
    public const int ErrorPreviewLength = 300;

    private readonly TeamBuilderDbContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboxOperations> _logger;

    public OutboxOperations(TeamBuilderDbContext context, TimeProvider timeProvider, ILogger<OutboxOperations> logger)
    {
        _context = context;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<FailedOutboxMessage>> ListFailedAsync(int take = 50, CancellationToken cancellationToken = default)
    {
        var rows = await _context.OutboxMessages
            .AsNoTracking()
            .Where(m => m.Status == OutboxMessageStatus.Failed)
            .OrderByDescending(m => m.ProcessedAtUtc)
            .Take(Math.Clamp(take, 1, 1000))
            .Select(m => new FailedOutboxMessage(
                m.Id, m.Type, m.AggregateId, m.OccurredAtUtc, m.ProcessedAtUtc,
                m.AttemptCount, m.PriorAttemptCount, m.ReplayCount, m.LastReplayedAtUtc, m.LastError))
            .ToListAsync(cancellationToken);
        return rows.Select(r => r with { LastError = Preview(r.LastError) }).ToList();
    }

    public async Task<OutboxCounts> CountAsync(CancellationToken cancellationToken = default)
    {
        var counts = await _context.OutboxMessages
            .GroupBy(m => m.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        int Of(OutboxMessageStatus status) => counts.FirstOrDefault(c => c.Key == status)?.Count ?? 0;
        return new OutboxCounts(Of(OutboxMessageStatus.Pending), Of(OutboxMessageStatus.Processing), Of(OutboxMessageStatus.Completed), Of(OutboxMessageStatus.Failed));
    }

    public async Task<OutboxReplayResult> ReplayAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var updated = await _context.OutboxMessages
            .Where(m => m.Id == messageId && m.Status == OutboxMessageStatus.Failed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxMessageStatus.Pending)
                .SetProperty(m => m.PriorAttemptCount, m => m.PriorAttemptCount + m.AttemptCount)
                .SetProperty(m => m.AttemptCount, 0)
                .SetProperty(m => m.ReplayCount, m => m.ReplayCount + 1)
                .SetProperty(m => m.LastReplayedAtUtc, now)
                .SetProperty(m => m.NextAttemptAtUtc, now)
                .SetProperty(m => m.ProcessedAtUtc, (DateTime?)null)
                .SetProperty(m => m.LockOwner, (string?)null)
                .SetProperty(m => m.LockExpiresAtUtc, (DateTime?)null),
                cancellationToken);

        if (updated == 1)
        {
            RefillTelemetry.OutboxReplayed.Add(1);
            _logger.LogWarning("OutboxReplayed: message {MessageId} was requeued by an operator.", messageId);
            return OutboxReplayResult.Requeued;
        }

        var status = await _context.OutboxMessages
            .Where(m => m.Id == messageId)
            .Select(m => (OutboxMessageStatus?)m.Status)
            .FirstOrDefaultAsync(cancellationToken);
        return status is null ? OutboxReplayResult.NotFound : OutboxReplayResult.NotFailed;
    }

    private static string? Preview(string? error) =>
        error is null || error.Length <= ErrorPreviewLength ? error : error[..ErrorPreviewLength] + "…";
}

public sealed record FailedOutboxMessage(
    Guid Id,
    string Type,
    Guid AggregateId,
    DateTime OccurredAtUtc,
    DateTime? FailedAtUtc,
    int AttemptCount,
    int PriorAttemptCount,
    int ReplayCount,
    DateTime? LastReplayedAtUtc,
    string? LastError);

public sealed record OutboxCounts(int Pending, int Processing, int Completed, int Failed);

public enum OutboxReplayResult
{
    Requeued,
    NotFound,
    NotFailed
}
