using System.Diagnostics.Metrics;

namespace TeamBuilder.Infrastructure.Outbox;

/// <summary>
/// Metrics for the rapid-refill loop (meter <c>TeamBuilder.Refill</c>), exported by whatever
/// OpenTelemetry/dotnet-counters listener the host attaches. Counters carry only low-cardinality
/// tags (event type, reason, outcome): never player, venue or location data.
/// </summary>
public static class RefillTelemetry
{
    public const string MeterName = "TeamBuilder.Refill";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>A vacancy event committed with its roster change. Tag: reason.</summary>
    public static readonly Counter<long> VacancyOpened = Meter.CreateCounter<long>("teambuilder.refill.vacancy_opened");

    /// <summary>An outbox message completed with at least one side effect. Tag: type.</summary>
    public static readonly Counter<long> OutboxProcessed = Meter.CreateCounter<long>("teambuilder.outbox.processed");

    /// <summary>An outbox message completed with nothing to do. Tags: type, outcome.</summary>
    public static readonly Counter<long> OutboxSkipped = Meter.CreateCounter<long>("teambuilder.outbox.skipped");

    /// <summary>A failed attempt. Tags: type, final (true when the message is now Failed).</summary>
    public static readonly Counter<long> OutboxFailed = Meter.CreateCounter<long>("teambuilder.outbox.failed");

    /// <summary>In-app notifications inserted. Tag: type.</summary>
    public static readonly Counter<long> NotificationCreated = Meter.CreateCounter<long>("teambuilder.notifications.created");
}
