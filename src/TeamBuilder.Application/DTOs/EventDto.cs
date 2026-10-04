using System.ComponentModel.DataAnnotations;
using TeamBuilder.Application.Validation;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Application.DTOs;

/// <summary>
/// An event occurrence as served by <c>/api/v1/events</c>. <see cref="EventDateUtc"/> and
/// <see cref="Location"/> are kept for compatibility with existing clients.
/// </summary>
public class EventDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Compatibility alias: always equal to <see cref="ScheduledStartUtc"/>.</summary>
    public DateTime EventDateUtc { get; set; }

    public DateTime ScheduledStartUtc { get; set; }
    public DateTime? ScheduledEndUtc { get; set; }
    public EventStatus Status { get; set; }
    public string? Category { get; set; }
    public string? Tags { get; set; }

    /// <summary>
    /// Compatibility field: the venue's name when the occurrence has a venue, otherwise the
    /// legacy free-text location.
    /// </summary>
    public string? Location { get; set; }

    public Guid? SeriesId { get; set; }
    public Guid? VenueId { get; set; }
    public bool IsDetached { get; set; }
    public string? Region { get; set; }
    public int MaxParticipants { get; set; }
    public int CurrentParticipantCount { get; set; }
    public Guid? TeamId { get; set; }
    public string? TeamName { get; set; }
    public Guid? HostId { get; set; }
    public string? HostUsername { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class CreateEventDto
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    /// <summary>Start time of the occurrence (UTC); stored as its scheduled start.</summary>
    [Required]
    public DateTime? EventDateUtc { get; set; }

    [StringLength(100)]
    public string? Category { get; set; }

    [StringLength(500)]
    public string? Tags { get; set; }

    /// <summary>Free-text display location; stored as legacy location text, never geocoded.</summary>
    [StringLength(200)]
    public string? Location { get; set; }

    [StringLength(100)]
    public string? Region { get; set; }

    [Range(1, 100000)]
    public int MaxParticipants { get; set; } = 50;

    /// <summary>
    /// Optional team association. When null the event is standalone; when set, only the
    /// team's owner may create it. Immutable after creation (UpdateEventDto has no TeamId).
    /// </summary>
    [NonEmptyGuid]
    public Guid? TeamId { get; set; }
}

public class UpdateEventDto
{
    [StringLength(200, MinimumLength = 1)]
    public string? Name { get; set; }

    [StringLength(2000)]
    public string? Description { get; set; }

    public DateTime? EventDateUtc { get; set; }

    [EnumDataType(typeof(EventStatus))]
    public EventStatus? Status { get; set; }

    [StringLength(100)]
    public string? Category { get; set; }

    [StringLength(500)]
    public string? Tags { get; set; }

    [StringLength(200)]
    public string? Location { get; set; }

    [StringLength(100)]
    public string? Region { get; set; }

    [Range(1, 100000)]
    public int? MaxParticipants { get; set; }
}
