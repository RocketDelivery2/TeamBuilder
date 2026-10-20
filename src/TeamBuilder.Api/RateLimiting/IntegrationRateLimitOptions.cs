using System.ComponentModel.DataAnnotations;

namespace TeamBuilder.Api.RateLimiting;

/// <summary>
/// Per-key fixed-window limit on the public integration API (<c>RateLimiting:Integration</c>),
/// partitioned by the authenticated API key id (the client IP only as a fallback). A rejected
/// call is 429 with Retry-After. In-memory per instance.
/// </summary>
public sealed class IntegrationRateLimitOptions
{
    public const string SectionName = "RateLimiting:Integration";
    public const string PolicyName = "integration";

    [Range(1, 100000)]
    public int PermitLimit { get; set; } = 120;

    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;
}

/// <summary>
/// Per-caller fixed-window limit on creating and revoking integration API keys
/// (<c>RateLimiting:ApiKeys</c>), partitioned like the subscription limit. The active key cap
/// bounds live keys; this bounds churn (rows created by a create/revoke loop).
/// </summary>
public sealed class ApiKeyChangeRateLimitOptions
{
    public const string SectionName = "RateLimiting:ApiKeys";
    public const string PolicyName = "api-key-changes";

    [Range(1, 100000)]
    public int PermitLimit { get; set; } = 20;

    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 3600;
}

/// <summary>
/// Per-client-IP fixed-window limit on rejected integration API keys
/// (<c>RateLimiting:IntegrationAuthFailures</c>). Checked by the API key scheme before its
/// database lookup, so guessing keys cannot drive unbounded lookups; valid keys never spend it.
/// Once spent, further key attempts from that IP get 429 with Retry-After. In-memory per instance.
/// </summary>
public sealed class IntegrationAuthFailureRateLimitOptions
{
    public const string SectionName = "RateLimiting:IntegrationAuthFailures";

    [Range(1, 100000)]
    public int PermitLimit { get; set; } = 30;

    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;
}
