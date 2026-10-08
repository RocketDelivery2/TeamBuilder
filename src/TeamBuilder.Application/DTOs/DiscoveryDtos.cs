using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Application.DTOs;

/// <summary>
/// One <c>GET /api/v1/discover/occurrences</c> request, with the point, radius, activity and
/// page size already validated. The search point is transient: it is used for this query only
/// and is never stored or logged. <see cref="FromUtc"/>/<see cref="ToUtc"/> are as supplied
/// (UTC); the service applies the defaults, or the window carried by <see cref="Cursor"/>, so a
/// "from now" search keeps one fixed window across its pages.
/// </summary>
public sealed record DiscoverOccurrencesQuery(
    double Latitude,
    double Longitude,
    double RadiusMiles,
    string? Activity,
    DateTime? FromUtc,
    DateTime? ToUtc,
    bool OpenOnly,
    int PageSize,
    string? Cursor)
{
    public const double DefaultRadiusMiles = 15;
    public const double MaxRadiusMiles = 100;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;
    public static readonly TimeSpan DefaultRange = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(30);

    /// <summary>The radius presets the clients offer; any radius up to the maximum is accepted.</summary>
    public static readonly IReadOnlyList<int> RadiusPresetsMiles = [15, 25, 50];
}

public class DiscoveredOccurrencePageDto
{
    public IReadOnlyList<DiscoveredOccurrenceDto> Items { get; set; } = [];

    /// <summary>Pass back as <c>cursor</c> with the same search parameters; null on the last page.</summary>
    public string? NextCursor { get; set; }
}

/// <summary>
/// One nearby occurrence. Purpose-built for discovery: public fields only, no participant list,
/// no identity metadata.
/// </summary>
public class DiscoveredOccurrenceDto
{
    public Guid OccurrenceId { get; set; }
    public Guid? SeriesId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public EventStatus Status { get; set; }
    public DateTime ScheduledStartUtc { get; set; }
    public DateTime? ScheduledEndUtc { get; set; }

    /// <summary>The venue's IANA time zone, for showing the start in local time.</summary>
    public string? TimeZoneId { get; set; }

    public OccurrenceVenueDto Venue { get; set; } = new();
    public DiscoveredRosterDto Roster { get; set; } = new();

    /// <summary>Null for an anonymous or unlinked caller.</summary>
    public DiscoveryViewerDto? Viewer { get; set; }
}

/// <summary>Roster totals across every requirement of the occurrence.</summary>
public class DiscoveredRosterDto
{
    /// <summary>Sum of RequiredCount.</summary>
    public int TotalRequiredCount { get; set; }

    /// <summary>Current supply-status assignments attached to a requirement.</summary>
    public int TotalSupplyCount { get; set; }

    /// <summary>Sum of max(0, Required - Supply) per requirement.</summary>
    public int TotalOpenQuantity { get; set; }

    /// <summary>At least one requirement and every requirement satisfied.</summary>
    public bool IsRosterReady { get; set; }

    /// <summary>No requirement has an open spot (same as ready while overfill is impossible).</summary>
    public bool IsFull { get; set; }
}

public class DiscoveryViewerDto
{
    public bool IsHost { get; set; }

    /// <summary>The caller holds a current (supply-status) assignment.</summary>
    public bool IsParticipating { get; set; }

    public RosterAssignmentStatus? ParticipationStatus { get; set; }
}
