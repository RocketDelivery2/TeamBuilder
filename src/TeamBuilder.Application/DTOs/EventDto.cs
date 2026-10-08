using System.ComponentModel.DataAnnotations;
using TeamBuilder.Application.Validation;
using TeamBuilder.Domain;
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

public class CreateEventDto : IValidatableObject
{
    /// <summary>Upper bound of <see cref="RosterRequirements"/> in one create request.</summary>
    public const int MaxRosterRequirements = 50;

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

    /// <summary>
    /// Optional venue (from <c>POST /api/v1/venues</c>). Any Public or Virtual venue may be used;
    /// a Private venue only by the player who created it. Only occurrences with a physical venue
    /// that has coordinates appear in radius discovery; <see cref="Location"/> stays free text.
    /// </summary>
    [NonEmptyGuid]
    public Guid? VenueId { get; set; }

    /// <summary>Optional scheduled end (UTC); must be after <see cref="EventDateUtc"/>.</summary>
    public DateTime? ScheduledEndUtc { get; set; }

    /// <summary>
    /// Optional initial roster requirements, created atomically with the occurrence. Omitted or
    /// empty keeps the event without a roster (never roster-ready). Each entry follows the
    /// <c>POST …/roster/requirements</c> rules; role codes must be distinct after normalization.
    /// Nothing here is activity-specific: a basketball client sends <c>participant</c> x 10.
    /// </summary>
    [MaxLength(MaxRosterRequirements)]
    public List<CreateRosterRequirementDto>? RosterRequirements { get; set; }

    /// <summary>
    /// Whether the host also plays. False or omitted: the host is organizer only and holds no
    /// spot (supply 0). True: the host gets a normal Confirmed assignment on a requirement in the
    /// same commit, through the same capacity invariant as a self-claim.
    /// </summary>
    public bool? HostParticipates { get; set; }

    /// <summary>
    /// With <see cref="HostParticipates"/> and several requirements: the role code of the
    /// requirement the host fills. Optional when there is exactly one requirement.
    /// </summary>
    [StringLength(RosterRoleCodes.MaxLength)]
    public string? HostRoleCode { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ScheduledEndUtc is { } end && EventDateUtc is { } start && end <= start)
            yield return new ValidationResult("ScheduledEndUtc must be after EventDateUtc.", [nameof(ScheduledEndUtc)]);

        if (RosterRequirements?.Any(r => r is null) == true)
            yield return new ValidationResult("RosterRequirements must not contain null entries.", [nameof(RosterRequirements)]);

        if (HostRoleCode is not null && !RosterRoleCodes.TryNormalize(HostRoleCode, out _, out var error))
            yield return new ValidationResult(error, [nameof(HostRoleCode)]);
    }
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
