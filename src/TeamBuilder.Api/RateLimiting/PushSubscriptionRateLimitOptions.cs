using System.ComponentModel.DataAnnotations;

namespace TeamBuilder.Api.RateLimiting;

/// <summary>
/// Per-caller fixed-window limit on browser push registration changes
/// (<c>RateLimiting:PushSubscriptions</c>), partitioned like the subscription limit: by the
/// authenticated external identity, the client IP only as a fallback. In-memory per instance.
/// </summary>
public sealed class PushSubscriptionRateLimitOptions
{
    public const string SectionName = "RateLimiting:PushSubscriptions";
    public const string PolicyName = "push-subscriptions";

    [Range(1, 100000)]
    public int PermitLimit { get; set; } = 20;

    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;
}
