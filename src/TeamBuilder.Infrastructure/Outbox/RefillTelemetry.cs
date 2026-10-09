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

    // ── Web Push (tags: service = fcm/mozilla/apple/wns/other, category = fixed failure class) ──

    /// <summary>A send attempt to a push service.</summary>
    public static readonly Counter<long> PushAttempted = Meter.CreateCounter<long>("teambuilder.push.attempted");

    /// <summary>The push service accepted the message (2xx). Not proof of display.</summary>
    public static readonly Counter<long> PushAccepted = Meter.CreateCounter<long>("teambuilder.push.accepted");

    /// <summary>A delivery that will not be retried (gone, rejected, invalid keys, retries exhausted).</summary>
    public static readonly Counter<long> PushPermanentFailure = Meter.CreateCounter<long>("teambuilder.push.permanent_failure");

    /// <summary>A failed attempt that will be retried (429, 5xx, timeout, network).</summary>
    public static readonly Counter<long> PushTransientFailure = Meter.CreateCounter<long>("teambuilder.push.transient_failure");

    /// <summary>A delivery skipped before sending (subscription gone/inactive/reassigned, alert expired). Tag: reason.</summary>
    public static readonly Counter<long> PushAbandoned = Meter.CreateCounter<long>("teambuilder.push.abandoned");

    /// <summary>A subscription disabled by delivery feedback. Tag: reason.</summary>
    public static readonly Counter<long> PushSubscriptionDisabled = Meter.CreateCounter<long>("teambuilder.push.subscription_disabled");

    /// <summary>A notification opened by its player. Tag: via (push/inApp).</summary>
    public static readonly Counter<long> NotificationOpened = Meter.CreateCounter<long>("teambuilder.notifications.opened");

    // ── Outbox operations ──

    /// <summary>Rows deleted by retention. Tag: table.</summary>
    public static readonly Counter<long> OutboxPurged = Meter.CreateCounter<long>("teambuilder.outbox.purged");

    /// <summary>Failed messages requeued by an operator.</summary>
    public static readonly Counter<long> OutboxReplayed = Meter.CreateCounter<long>("teambuilder.outbox.replayed");

    // ── Refill funnel timings (milliseconds) ──

    /// <summary>Vacancy committed → in-app notification inserted.</summary>
    public static readonly Histogram<double> VacancyToNotification = Meter.CreateHistogram<double>("teambuilder.refill.vacancy_to_notification", "ms");

    /// <summary>Vacancy committed → push service accepted the message.</summary>
    public static readonly Histogram<double> VacancyToPushAccepted = Meter.CreateHistogram<double>("teambuilder.refill.vacancy_to_push_accepted", "ms");

    /// <summary>Push notification clicked → game shown (client measured). </summary>
    public static readonly Histogram<double> PushClickToGameOpen = Meter.CreateHistogram<double>("teambuilder.refill.push_click_to_game_open", "ms");

    /// <summary>Game opened from a notification → claim attempt. Tag: outcome (claimed/conflict).</summary>
    public static readonly Histogram<double> GameOpenToClaimAttempt = Meter.CreateHistogram<double>("teambuilder.refill.game_open_to_claim_attempt", "ms");

    /// <summary>Vacancy committed → a notified player's claim confirmed (the spot refilled).</summary>
    public static readonly Histogram<double> VacancyToReplacement = Meter.CreateHistogram<double>("teambuilder.refill.vacancy_to_replacement", "ms");

    internal static double Milliseconds(DateTime fromUtc, DateTime toUtc) => Math.Max(0, (toUtc - fromUtc).TotalMilliseconds);
}
