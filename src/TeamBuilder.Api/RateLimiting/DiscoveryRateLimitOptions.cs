using System.ComponentModel.DataAnnotations;

namespace TeamBuilder.Api.RateLimiting;

/// <summary>
/// Per-client fixed-window limit on public discovery (<c>RateLimiting:Discovery</c>). It only
/// protects the read path from bursts; it is in-memory per API instance and is never an
/// authority for roster claims, which stay guarded by the database.
/// </summary>
public sealed class DiscoveryRateLimitOptions
{
    public const string SectionName = "RateLimiting:Discovery";
    public const string PolicyName = "discovery";

    /// <summary>Requests allowed per client per window.</summary>
    [Range(1, 100000)]
    public int PermitLimit { get; set; } = 60;

    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;
}
