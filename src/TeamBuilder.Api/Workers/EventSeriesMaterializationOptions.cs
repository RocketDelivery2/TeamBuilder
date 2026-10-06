using System.ComponentModel.DataAnnotations;

namespace TeamBuilder.Api.Workers;

/// <summary>Settings for <see cref="EventSeriesMaterializationWorker"/> (section "EventSeriesMaterialization").</summary>
public sealed class EventSeriesMaterializationOptions
{
    public const string SectionName = "EventSeriesMaterialization";

    /// <summary>Hourly: the horizon is 21 days, so this is not latency-sensitive.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(1);

    public const int DefaultBatchSize = 100;

    /// <summary>When false the worker starts and exits without doing anything.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay between passes. The first pass runs at startup.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "1.00:00:00")]
    public TimeSpan Interval { get; set; } = DefaultInterval;

    /// <summary>Active series ids read per keyset page.</summary>
    [Range(1, 10000)]
    public int BatchSize { get; set; } = DefaultBatchSize;
}
