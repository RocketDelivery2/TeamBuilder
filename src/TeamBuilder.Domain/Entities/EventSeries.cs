using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Domain.Entities;

/// <summary>
/// A recurrence/scheduling definition from which occurrences are produced. Persistence
/// foundation only: there is no series API, recurrence parsing or materialization yet.
/// </summary>
public class EventSeries : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Category { get; set; }
    public string? Tags { get; set; }

    public Guid? TeamId { get; set; }
    public Team? Team { get; set; }

    public Guid? HostId { get; set; }
    public Player? Host { get; set; }

    public Guid? VenueId { get; set; }
    public Venue? Venue { get; set; }

    /// <summary>Wall-clock start time in <see cref="TimeZoneId"/>.</summary>
    public TimeOnly LocalStartTime { get; set; }

    public int DurationMinutes { get; set; }

    /// <summary>IANA time zone identifier the series is scheduled in.</summary>
    public string TimeZoneId { get; set; } = string.Empty;

    public string RecurrenceRule { get; set; } = string.Empty;

    public DateOnly SeriesStartDate { get; set; }
    public DateOnly? SeriesEndDate { get; set; }

    public EventSeriesStatus Status { get; set; }

    public ICollection<EventOccurrence> Occurrences { get; set; } = new List<EventOccurrence>();
}
