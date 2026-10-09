namespace TeamBuilder.Domain.Entities;

/// <summary>
/// One browser's standard Web Push subscription, owned by the linked player who registered it.
/// A player may have several (phone, laptop, a second browser). <see cref="Endpoint"/>,
/// <see cref="P256dh"/> and <see cref="Auth"/> are delivery credentials: anyone holding them
/// can send that browser notifications, so they never leave the server (no DTO, log, payload
/// or error message carries them). The endpoint is unique through <see cref="EndpointHash"/>:
/// it identifies the browser installation, so registering it again (by the same or another
/// player signed in on that browser) updates this row instead of adding one.
/// </summary>
/// <remarks>
/// Web Push is delivery only. A subscription never reserves capacity and is never consulted
/// by the roster; the in-app notification stays the durable record of an alert.
/// </remarks>
public class PushSubscription : BaseEntity
{
    public Guid PlayerId { get; set; }
    public Player Player { get; set; } = null!;

    /// <summary>The push service URL of this browser installation (absolute https).</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>SHA-256 of <see cref="Endpoint"/>: the unique key (an endpoint can exceed index key limits).</summary>
    public byte[] EndpointHash { get; set; } = [];

    /// <summary>The browser's P-256 public key (base64url, 65 bytes uncompressed).</summary>
    public string P256dh { get; set; } = string.Empty;

    /// <summary>The browser's authentication secret (base64url, 16 bytes).</summary>
    public string Auth { get; set; } = string.Empty;

    /// <summary>A coarse browser family ("Chrome", "Firefox", "Safari", "Edge", "Other") for the device list.</summary>
    public string? UserAgentFamily { get; set; }

    /// <summary>When the browser last registered or confirmed this subscription.</summary>
    public DateTime LastSeenAtUtc { get; set; }

    /// <summary>The browser's own expiration time, when it gave one.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Consecutive failed deliveries; reset by an accepted one.</summary>
    public int FailureCount { get; set; }

    public DateTime? DisabledAtUtc { get; set; }

    /// <summary>Why it stopped receiving pushes (see <see cref="PushSubscriptionDisabledReasons"/>).</summary>
    public string? DisabledReason { get; set; }
}

/// <summary>Stable values of <see cref="PushSubscription.DisabledReason"/>.</summary>
public static class PushSubscriptionDisabledReasons
{
    /// <summary>The player turned alerts off on this browser.</summary>
    public const string Unregistered = "Unregistered";

    /// <summary>The push service answered 404/410: the subscription no longer exists.</summary>
    public const string Expired = "Expired";

    /// <summary>The browser rotated to a new endpoint, which replaced this one.</summary>
    public const string Replaced = "Replaced";

    /// <summary>Too many consecutive failed deliveries, or the push service rejected it as malformed.</summary>
    public const string Rejected = "Rejected";

    /// <summary>The player registered more browsers than allowed; the least recently seen was retired.</summary>
    public const string Evicted = "Evicted";
}
