using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Domain.Entities;

/// <summary>
/// A durable integration message, inserted in the same SaveChanges (and therefore the same SQL
/// Server transaction) as the state change it describes, then processed asynchronously by the
/// outbox worker. Delivery is at-least-once: a message can be handed to its handler more than
/// once (a lease expired, a completion was lost), so every handler side effect is idempotent.
/// </summary>
/// <remarks>
/// Deliberately not a <see cref="BaseEntity"/>: it has no RowVersion. Workers claim rows with
/// one locking UPDATE (UPDLOCK/READPAST) and complete them only while they still own the lease
/// (<see cref="LockOwner"/>), which is the concurrency guard.
/// </remarks>
public class OutboxMessage
{
    /// <summary>The event id: the same value as the payload's <c>eventId</c>.</summary>
    public Guid Id { get; set; }

    /// <summary>Stable external event type, e.g. <c>roster.vacancy.opened.v1</c>. Never a CLR type name.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The aggregate the event is about (for a vacancy: the occurrence).</summary>
    public Guid AggregateId { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    /// <summary>The versioned JSON contract of <see cref="Type"/>.</summary>
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>
    /// Unique: the same fact is never staged twice (for a vacancy, one per assignment that
    /// stopped holding supply, since an assignment ends at most once).
    /// </summary>
    public string DeduplicationKey { get; set; } = string.Empty;

    public OutboxMessageStatus Status { get; set; } = OutboxMessageStatus.Pending;

    /// <summary>Claims so far, including the current one; bounded by the worker's MaxAttempts.</summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Earliest time a Pending message may be claimed: the occurrence time for a new message, a
    /// backoff after a failed attempt. Lets a retry wait without a busy loop.
    /// </summary>
    public DateTime NextAttemptAtUtc { get; set; }

    /// <summary>The claim token of the worker holding the lease while Processing; null otherwise.</summary>
    public string? LockOwner { get; set; }

    /// <summary>When the current lease ends; an expired Processing message is reclaimed.</summary>
    public DateTime? LockExpiresAtUtc { get; set; }

    public DateTime? ProcessedAtUtc { get; set; }

    /// <summary>The last failure (exception type and message, truncated). No secrets or personal data.</summary>
    public string? LastError { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Times an operator requeued this message after it Failed (see the <c>outbox replay</c>
    /// maintenance command). Each replay moves <see cref="AttemptCount"/> into
    /// <see cref="PriorAttemptCount"/> and starts a fresh set of attempts.
    /// </summary>
    public int ReplayCount { get; set; }

    /// <summary>Attempts made before the latest replay; with <see cref="AttemptCount"/>, the full history.</summary>
    public int PriorAttemptCount { get; set; }

    public DateTime? LastReplayedAtUtc { get; set; }
}
