using System.ComponentModel.DataAnnotations;

namespace TeamBuilder.Api.RateLimiting;

/// <summary>
/// Per-caller fixed-window limit on "notify me" subscription changes
/// (<c>RateLimiting:Subscriptions</c>), partitioned by the authenticated external identity
/// (the client IP only when unauthenticated, which those routes reject anyway). In-memory per
/// API instance; it only damps toggling, it is not an authority for anything.
/// </summary>
public sealed class SubscriptionRateLimitOptions
{
    public const string SectionName = "RateLimiting:Subscriptions";
    public const string PolicyName = "subscriptions";

    [Range(1, 100000)]
    public int PermitLimit { get; set; } = 30;

    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;
}
