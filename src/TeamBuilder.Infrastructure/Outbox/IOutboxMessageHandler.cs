using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Infrastructure.Outbox;

/// <summary>
/// Handles one outbox message type. Called at least once per message, possibly concurrently
/// with a late duplicate after a lease expired, so every side effect must be idempotent. A
/// thrown exception is a failed attempt (retried with backoff up to MaxAttempts).
/// </summary>
public interface IOutboxMessageHandler
{
    /// <summary>The stable event type handled, e.g. <c>roster.vacancy.opened.v1</c>.</summary>
    string Type { get; }

    Task<OutboxHandlerResult> HandleAsync(OutboxMessage message, CancellationToken cancellationToken);
}

/// <param name="Effects">Side effects produced by this attempt (e.g. notifications inserted).</param>
/// <param name="Outcome">A short machine-readable outcome for logs and metrics, e.g. <c>Notified</c>, <c>RequirementFull</c>.</param>
public sealed record OutboxHandlerResult(int Effects, string Outcome);
