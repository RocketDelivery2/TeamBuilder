using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Domain.Entities;

/// <summary>
/// One concrete game/session. This is the entity formerly named <c>TeamEvent</c>; it still
/// persists to the <c>Events</c> table and is served by the <c>/api/v1/events</c> routes.
/// A one-off (pickup/community) occurrence has no <see cref="SeriesId"/>, and an occurrence
/// with no team association has no <see cref="TeamId"/>.
/// </summary>
public class EventOccurrence : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>When the occurrence starts (UTC). Formerly <c>EventDateUtc</c>.</summary>
    public DateTime ScheduledStartUtc { get; set; }

    /// <summary>
    /// When the occurrence ends (UTC), if known. Null for legacy events, whose end was never
    /// recorded; no duration is invented for them.
    /// </summary>
    public DateTime? ScheduledEndUtc { get; set; }

    public EventStatus Status { get; set; }
    public string? Category { get; set; }
    public string? Tags { get; set; }

    /// <summary>
    /// Free-text location carried over from the legacy <c>Location</c> column. It is display
    /// text only: it is never geocoded or turned into a <see cref="Venue"/>.
    /// </summary>
    public string? LegacyLocation { get; set; }

    public string? Region { get; set; }
    public int MaxParticipants { get; set; }
    public int CurrentParticipantCount { get; set; }

    public Guid? SeriesId { get; set; }
    public EventSeries? Series { get; set; }

    /// <summary>
    /// True when this occurrence was generated from its series but has since been edited
    /// independently of it. Always false for one-off and legacy occurrences.
    /// </summary>
    public bool IsDetached { get; set; }

    /// <summary>Position of this occurrence within its series, when it has one.</summary>
    public int? OccurrenceIndex { get; set; }

    public Guid? VenueId { get; set; }
    public Venue? Venue { get; set; }

    public Guid? TeamId { get; set; }
    public Team? Team { get; set; }
    public Guid? HostId { get; set; }
    public Player? Host { get; set; }

    /// <summary>Imported/raw roster provenance. Not live participation; see <see cref="RosterAssignments"/>.</summary>
    public ICollection<RosterEntry> RosterEntries { get; set; } = new List<RosterEntry>();

    /// <summary>Quantity-based roster demand for this occurrence.</summary>
    public ICollection<RosterRequirement> RosterRequirements { get; set; } = new List<RosterRequirement>();

    /// <summary>
    /// Live participation history. Supply-status rows are the authoritative participant source
    /// for roster operations; <see cref="CurrentParticipantCount"/> is legacy stored state.
    /// </summary>
    public ICollection<RosterAssignment> RosterAssignments { get; set; } = new List<RosterAssignment>();

    /// <summary>
    /// The location shown to API clients: the venue's name when a venue is attached,
    /// otherwise the legacy free-text location.
    /// </summary>
    public string? DisplayLocation => Venue != null ? Venue.Name : LegacyLocation;
}
