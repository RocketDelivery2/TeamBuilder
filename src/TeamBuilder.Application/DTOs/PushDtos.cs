using System.ComponentModel.DataAnnotations;

namespace TeamBuilder.Application.DTOs;

/// <summary>Whether this server sends Web Push, and the public VAPID key browsers subscribe with.</summary>
public class WebPushConfigDto
{
    public bool Enabled { get; set; }

    /// <summary>The application server key (base64url); null when Web Push is off.</summary>
    public string? VapidPublicKey { get; set; }
}

/// <summary>
/// The browser's <c>PushSubscription.toJSON()</c>, plus the endpoint it replaces when the
/// browser rotated it. These are delivery credentials: they are stored, never returned.
/// </summary>
public class RegisterPushSubscriptionDto
{
    [Required]
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Milliseconds since the Unix epoch, as browsers report it; usually null.</summary>
    public long? ExpirationTime { get; set; }

    [Required]
    public PushSubscriptionKeysDto Keys { get; set; } = new();

    /// <summary>The caller's previous endpoint on this browser, retired when it differs.</summary>
    public string? PreviousEndpoint { get; set; }
}

public class PushSubscriptionKeysDto
{
    [Required]
    public string P256dh { get; set; } = string.Empty;

    [Required]
    public string Auth { get; set; } = string.Empty;
}

public class UnregisterPushSubscriptionDto
{
    [Required]
    public string Endpoint { get; set; } = string.Empty;
}

/// <summary>One of the caller's browsers. Deliberately without the endpoint or keys.</summary>
public class PushSubscriptionDto
{
    public Guid Id { get; set; }
    public string? UserAgentFamily { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public bool IsActive { get; set; }
    public DateTime? DisabledAtUtc { get; set; }
    public string? DisabledReason { get; set; }
}

public sealed record PushSubscriptionRegistration(PushSubscriptionDto Subscription, bool Created);

/// <summary>The caller opened the game from a notification (refill-funnel metrics only).</summary>
public class NotificationOpenedDto
{
    /// <summary><c>push</c> (an OS/browser notification click) or <c>inApp</c> (the bell).</summary>
    public string? Via { get; set; }

    /// <summary>Client-measured milliseconds from the notification click to the game being shown.</summary>
    public int? ClickToOpenMs { get; set; }
}
