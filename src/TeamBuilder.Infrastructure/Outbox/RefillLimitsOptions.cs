using System.ComponentModel.DataAnnotations;

namespace TeamBuilder.Infrastructure.Outbox;

/// <summary>
/// Per-player fan-out guards (section <c>RefillLimits</c>). They bound what one account can
/// make the worker do; they never truncate the subscriber list of an occurrence: every
/// legitimate "notify me" on a game is notified. The per-player browser limit lives in
/// <see cref="WebPush.WebPushOptions.MaxDevicesPerPlayer"/>.
/// </summary>
public sealed class RefillLimitsOptions
{
    public const string SectionName = "RefillLimits";

    /// <summary>
    /// "Notify me" subscriptions a player may hold on games that are still open (Planned, Open,
    /// InProgress). Subscriptions on closed games do not count. A new one beyond this is 409
    /// SubscriptionLimitReached; existing ones keep working.
    /// </summary>
    [Range(1, 10000)]
    public int MaxActiveOccurrenceSubscriptionsPerPlayer { get; set; } = 50;
}
