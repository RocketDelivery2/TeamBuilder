using System.ComponentModel.DataAnnotations;
using TeamBuilder.Application.Validation;
using TeamBuilder.Domain;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Application.DTOs;

/// <summary>
/// Quantity-based roster demand of one occurrence, with derived supply. Served by
/// <c>/api/v1/events/{occurrenceId}/roster/requirements</c>.
/// </summary>
public class RosterRequirementDto
{
    public Guid Id { get; set; }
    public Guid OccurrenceId { get; set; }
    public string RoleCode { get; set; } = string.Empty;
    public string? DisplayPosition { get; set; }
    public string? SourceRoleLabel { get; set; }
    public int RequiredCount { get; set; }

    /// <summary>Assignments linked to this requirement in a supply status.</summary>
    public int SupplyCount { get; set; }

    /// <summary>max(0, RequiredCount - SupplyCount). Derived; never persisted.</summary>
    public int OpenQuantity { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class CreateRosterRequirementDto : IValidatableObject
{
    /// <summary>
    /// Canonical role code (TBRL code grammar), e.g. <c>guard</c> or <c>healer</c>. Omitted
    /// means generic demand and is stored as <c>participant</c>. Trimmed and lowercased.
    /// </summary>
    [StringLength(RosterRoleCodes.MaxLength)]
    public string? RoleCode { get; set; }

    [StringLength(100)]
    public string? DisplayPosition { get; set; }

    [StringLength(200)]
    public string? SourceRoleLabel { get; set; }

    [Required]
    [Range(1, 100000)]
    public int? RequiredCount { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (RoleCode is not null && !RosterRoleCodes.TryNormalize(RoleCode, out _, out var error))
            yield return new ValidationResult(error, [nameof(RoleCode)]);
    }
}

/// <summary>
/// Public view of a roster assignment: the player's id and username only, never email or
/// external identity data.
/// </summary>
public class RosterAssignmentDto
{
    public Guid Id { get; set; }
    public Guid OccurrenceId { get; set; }
    public Guid PlayerId { get; set; }
    public string Username { get; set; } = string.Empty;
    public Guid? RequirementId { get; set; }
    public string? RoleCode { get; set; }
    public string? SourceRoleLabel { get; set; }
    public RosterAssignmentStatus Status { get; set; }
    public RosterAssignmentSource Source { get; set; }
    public DateTime? ReservedAtUtc { get; set; }
    public DateTime? ConfirmedAtUtc { get; set; }
    public DateTime? CheckedInAtUtc { get; set; }
    public DateTime? ActivatedAtUtc { get; set; }
    public DateTime? DepartedAtUtc { get; set; }
    public RosterExitReason? ExitReason { get; set; }
    public Guid? ReplacedAssignmentId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>Host assignment of a player to an occurrence. Team membership is not required.</summary>
public class CreateRosterAssignmentDto : IValidatableObject
{
    [Required]
    [NonEmptyGuid]
    public Guid? PlayerId { get; set; }

    /// <summary>Optional requirement of the same occurrence that this assignment fills.</summary>
    [NonEmptyGuid]
    public Guid? RequirementId { get; set; }

    /// <summary>
    /// Optional canonical role code. Defaults to the requirement's role code when a requirement
    /// is given, and must match it when both are given.
    /// </summary>
    [StringLength(RosterRoleCodes.MaxLength)]
    public string? RoleCode { get; set; }

    [StringLength(200)]
    public string? SourceRoleLabel { get; set; }

    /// <summary>Initial status: a supply status (Reserved, Confirmed, CheckedIn, Active). Defaults to Confirmed.</summary>
    public RosterAssignmentStatus? Status { get; set; }

    /// <summary>
    /// Optional earlier assignment of the same occurrence that this one replaces. It must no
    /// longer hold supply (Departed, NoShow or Cancelled); it is left unchanged.
    /// </summary>
    [NonEmptyGuid]
    public Guid? ReplacedAssignmentId { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (RoleCode is not null && !RosterRoleCodes.TryNormalize(RoleCode, out _, out var error))
            yield return new ValidationResult(error, [nameof(RoleCode)]);

        if (Status is { } status && (!Enum.IsDefined(status) || !RosterState.IsSupply(status)))
        {
            yield return new ValidationResult(
                "Status must be Reserved, Confirmed, CheckedIn or Active for a new assignment.",
                [nameof(Status)]);
        }
    }
}
