using System.ComponentModel.DataAnnotations;

namespace TeamBuilder.Infrastructure.Outbox;

/// <summary>
/// Retention of finished refill-delivery rows (section <c>OutboxMaintenance</c>). Only
/// terminal history is purged: Completed outbox messages, finished push deliveries and
/// disabled push subscriptions. Pending, Processing and Failed outbox messages are never
/// touched unless <see cref="FailedRetention"/> is set explicitly.
/// </summary>
public sealed class OutboxMaintenanceOptions
{
    public const string SectionName = "OutboxMaintenance";

    /// <summary>When false the retention worker starts and exits (the CLI <c>outbox purge</c> still works).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Time between retention passes.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Completed outbox messages older than this (by ProcessedAtUtc) are deleted.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "3650.00:00:00")]
    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Accepted/Failed/Abandoned push deliveries older than this (by CompletedAtUtc) are deleted.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "3650.00:00:00")]
    public TimeSpan PushDeliveryRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Disabled push subscriptions (dead credentials) older than this are deleted.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "3650.00:00:00")]
    public TimeSpan DisabledPushSubscriptionRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Optional, off by default: Failed outbox messages older than this are deleted too. Leave
    /// unset to keep every failure until an operator replays or removes it.
    /// </summary>
    public TimeSpan? FailedRetention { get; set; }

    /// <summary>Rows per DELETE statement (each its own short transaction).</summary>
    [Range(1, 10000)]
    public int BatchSize { get; set; } = 1000;

    /// <summary>Upper bound of DELETE statements per table per pass, so one pass never runs unbounded.</summary>
    [Range(1, 100000)]
    public int MaxBatchesPerRun { get; set; } = 100;
}
