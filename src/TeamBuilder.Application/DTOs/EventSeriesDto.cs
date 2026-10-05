using System.ComponentModel.DataAnnotations;
using TeamBuilder.Application.Scheduling;
using TeamBuilder.Application.Validation;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Application.DTOs;

/// <summary>
/// A recurring event series as served by <c>/api/v1/event-series</c>. Occurrences are not
/// embedded; they are listed by <c>GET /api/v1/event-series/{id}/occurrences</c>.
/// </summary>
public class EventSeriesDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Category { get; set; }
    public string? Tags { get; set; }
    public Guid? TeamId { get; set; }
    public Guid? HostId { get; set; }
    public Guid? VenueId { get; set; }
    public TimeOnly LocalStartTime { get; set; }
    public int DurationMinutes { get; set; }
    public string TimeZoneId { get; set; } = string.Empty;
    public string RecurrenceRule { get; set; } = string.Empty;
    public DateOnly SeriesStartDate { get; set; }
    public DateOnly? SeriesEndDate { get; set; }
    public EventSeriesStatus Status { get; set; }
    public int MaxParticipants { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class CreateEventSeriesDto : IValidatableObject
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    [StringLength(100)]
    public string? Category { get; set; }

    [StringLength(500)]
    public string? Tags { get; set; }

    /// <summary>Optional team association; when set, only the team's owner may create the series.</summary>
    [NonEmptyGuid]
    public Guid? TeamId { get; set; }

    [NonEmptyGuid]
    public Guid? VenueId { get; set; }

    /// <summary>Local wall-clock start time in the series time zone, e.g. <c>17:00:00</c>.</summary>
    [Required]
    public TimeOnly? LocalStartTime { get; set; }

    /// <summary>1 minute to 1 week.</summary>
    [Required]
    [Range(1, 10080)]
    public int? DurationMinutes { get; set; }

    /// <summary>
    /// IANA time zone id. Optional only when the venue has a time zone, in which case a
    /// supplied value must equal the venue's exactly.
    /// </summary>
    [StringLength(IanaTimeZone.MaxLength)]
    public string? TimeZoneId { get; set; }

    /// <summary>RRULE value in the V0.1 subset, e.g. <c>FREQ=WEEKLY;BYDAY=TU</c>.</summary>
    [Required]
    [StringLength(1000)]
    public string RecurrenceRule { get; set; } = string.Empty;

    [Required]
    public DateOnly? SeriesStartDate { get; set; }

    /// <summary>Inclusive last local date; null for an indefinite series.</summary>
    public DateOnly? SeriesEndDate { get; set; }

    [Required]
    [Range(1, 100000)]
    public int? MaxParticipants { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrEmpty(RecurrenceRule) &&
            !RecurrenceRuleParser.TryParse(RecurrenceRule, out _, out var ruleError))
        {
            yield return new ValidationResult(ruleError, [nameof(RecurrenceRule)]);
        }

        if (TimeZoneId is not null && !IanaTimeZone.TryResolve(TimeZoneId, out _, out var timeZoneError))
            yield return new ValidationResult(timeZoneError, [nameof(TimeZoneId)]);

        if (SeriesStartDate is { } start && SeriesEndDate is { } end && end < start)
            yield return new ValidationResult("SeriesEndDate must be on or after SeriesStartDate.", [nameof(SeriesEndDate)]);
    }
}
