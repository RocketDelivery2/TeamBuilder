using System.ComponentModel.DataAnnotations;

namespace TeamBuilder.Infrastructure.Outbox;

/// <summary>Settings of the outbox worker (section "Outbox").</summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>When false the worker starts and exits; messages stay Pending until enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Wait between polls when the last batch was not full. A full batch polls again at once.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:10:00")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Messages claimed per batch.</summary>
    [Range(1, 500)]
    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// How long a claim is exclusive. A worker that crashes or stalls loses the batch to another
    /// instance after this; it must comfortably exceed the time one batch takes.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Claims per message, the first included, before it is marked Failed.</summary>
    [Range(1, 100)]
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Backoff after a failed attempt n: RetryBaseDelay × 2^(n-1), capped at <see cref="RetryMaxDelay"/>.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(5);

    [Range(typeof(TimeSpan), "00:00:00", "1.00:00:00")]
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan RetryDelayFor(int attempt)
    {
        var factor = Math.Pow(2, Math.Clamp(attempt - 1, 0, 30));
        var delay = TimeSpan.FromTicks((long)Math.Min(RetryBaseDelay.Ticks * factor, RetryMaxDelay.Ticks));
        return delay;
    }
}
