using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Domain.Entities;

/// <summary>
/// The delivery ledger of one in-app notification to one push subscription: what makes Web
/// Push best effort but not endless. It is inserted in the same commit as its notification, so
/// a replayed outbox message (which creates no new notification) creates no new deliveries,
/// and each row is sent by one worker at a time under a lease, retried with backoff on
/// transient failures and finished for good on permanent ones. A dead browser fails only its
/// own row; the outbox message and every other device are unaffected.
/// </summary>
/// <remarks>
/// Like <see cref="OutboxMessage"/> it is not a <see cref="BaseEntity"/>: the lease
/// (<see cref="LockOwner"/>) is the concurrency guard. <see cref="PushSubscriptionId"/> has no
/// foreign key, so deleting a subscription never fights the notification cascade; a delivery
/// whose subscription is gone or inactive is abandoned when claimed.
/// </remarks>
public class PushDelivery
{
    public Guid Id { get; set; }

    public Guid InAppNotificationId { get; set; }

    public Guid PushSubscriptionId { get; set; }

    public PushDeliveryStatus Status { get; set; } = PushDeliveryStatus.Pending;

    /// <summary>Send attempts so far, including the current one.</summary>
    public int AttemptCount { get; set; }

    public DateTime NextAttemptAtUtc { get; set; }

    public string? LockOwner { get; set; }

    public DateTime? LockExpiresAtUtc { get; set; }

    /// <summary>When the vacancy committed (the source event's time), for end-to-end latency metrics.</summary>
    public DateTime SourceOccurredAtUtc { get; set; }

    /// <summary>The push service's last HTTP status, if it answered.</summary>
    public int? LastStatusCode { get; set; }

    /// <summary>A fixed failure category (e.g. <c>Gone</c>, <c>Network</c>); never a URL, key or response body.</summary>
    public string? LastError { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When it reached a terminal status.</summary>
    public DateTime? CompletedAtUtc { get; set; }
}
