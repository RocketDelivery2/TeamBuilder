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

/// <summary>
/// Readiness projection of one occurrence, served by <c>GET /api/v1/events/{occurrenceId}/roster</c>.
/// Computed from supply-status assignments only; the legacy
/// <c>EventDto.CurrentParticipantCount</c> and <c>MaxParticipants</c> are never consulted.
/// </summary>
public class RosterSummaryDto
{
    public Guid OccurrenceId { get; set; }

    /// <summary>Sum of RequiredCount over all requirements (0 when there are none).</summary>
    public int RequiredCount { get; set; }

    /// <summary>All supply-status assignments, including ones linked to no requirement.</summary>
    public int SupplyCount { get; set; }

    /// <summary>Sum of per-requirement open quantities; surplus on one requirement never offsets another.</summary>
    public int OpenQuantity { get; set; }

    /// <summary>
    /// True only when at least one requirement exists and every requirement has zero open
    /// quantity. An occurrence without requirements is never ready.
    /// </summary>
    public bool IsRosterReady { get; set; }

    public IReadOnlyList<RosterRequirementDto> Requirements { get; set; } = [];

    /// <summary>Every assignment, history included, in creation order.</summary>
    public IReadOnlyList<RosterAssignmentDto> Assignments { get; set; } = [];
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

    /// <summary>The player's public display name (also exposed by public player reads).</summary>
    public string? DisplayName { get; set; }

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

/// <summary>
/// A player's self-claim of open quantity on one requirement of an occurrence. The claimant is
/// always the authenticated caller; team membership is not required.
/// </summary>
public class ClaimRosterSpotDto
{
    /// <summary>The requirement of this occurrence to claim open quantity on.</summary>
    [Required]
    [NonEmptyGuid]
    public Guid? RequirementId { get; set; }

    /// <summary>
    /// Optional replacement lineage: an earlier assignment of the same occurrence and the same
    /// requirement that no longer holds supply. Generic refill does not need it.
    /// </summary>
    [NonEmptyGuid]
    public Guid? ReplacesAssignmentId { get; set; }
}

/// <summary>
/// Outcome of a self-claim. <see cref="Created"/> is false when the caller already held a live
/// assignment on the same requirement and that existing assignment was returned (safe retry).
/// </summary>
public sealed record RosterClaimResult(RosterAssignmentDto Assignment, bool Created);

/// <summary>
/// One occurrence on the caller's own schedule (<c>GET /api/v1/players/me/occurrences</c>),
/// shaped for a thin phone or web client: when and where, who hosts, the caller's own
/// assignment, and how full its requirement and the whole roster are. Only public player
/// fields appear (username, display name), never email or external identity data. This is
/// occurrence participation, not team membership.
/// </summary>
public class PlayerOccurrenceDto
{
    public Guid OccurrenceId { get; set; }
    public Guid? SeriesId { get; set; }

    /// <summary>The optional team association; null for pickup games.</summary>
    public Guid? TeamId { get; set; }

    public string Name { get; set; } = string.Empty;
    public DateTime ScheduledStartUtc { get; set; }
    public DateTime? ScheduledEndUtc { get; set; }
    public EventStatus Status { get; set; }
    public Guid? VenueId { get; set; }

    /// <summary>The venue's name when a venue is attached, otherwise the free-text location.</summary>
    public string? Location { get; set; }

    public string? Category { get; set; }
    public Guid? HostPlayerId { get; set; }
    public string? HostUsername { get; set; }
    public string? HostDisplayName { get; set; }

    /// <summary>Whether the caller is the occurrence's host (administrative only; it holds no spot).</summary>
    public bool IsHost { get; set; }

    /// <summary>
    /// The caller's assignment. Null only for an organizer-only hosted occurrence listed through
    /// <c>includeHosted=true</c>; always set otherwise.
    /// </summary>
    public Guid? MyAssignmentId { get; set; }

    /// <summary>Status of <see cref="MyAssignmentId"/>; null exactly when it is.</summary>
    public RosterAssignmentStatus? MyAssignmentStatus { get; set; }

    /// <summary>The caller's role code: the assignment's, else its requirement's.</summary>
    public string? MyRoleCode { get; set; }

    public Guid? MyRequirementId { get; set; }

    /// <summary>RequiredCount of the caller's requirement; null when the assignment has none.</summary>
    public int? RequiredCount { get; set; }

    /// <summary>Supply-status assignments on the caller's requirement; null when it has none.</summary>
    public int? SupplyCount { get; set; }

    /// <summary>Open quantity of the caller's requirement; null when it has none.</summary>
    public int? OpenQuantity { get; set; }

    /// <summary>Readiness of the whole occurrence roster (every requirement filled).</summary>
    public bool IsRosterReady { get; set; }

    /// <summary>Sum of RequiredCount over every requirement of the occurrence (0 when none).</summary>
    public int TotalRequiredCount { get; set; }

    /// <summary>Supply-status assignments on the whole occurrence.</summary>
    public int TotalSupplyCount { get; set; }

    /// <summary>Sum of per-requirement open quantities of the whole occurrence.</summary>
    public int TotalOpenQuantity { get; set; }
}

/// <summary>
/// One page of the caller's schedule, nearest first. Pass <see cref="NextCursor"/> back as
/// <c>cursor</c> for the next page; it is null on the last page.
/// </summary>
public class PlayerOccurrencePageDto
{
    public IReadOnlyList<PlayerOccurrenceDto> Items { get; set; } = [];
    public string? NextCursor { get; set; }
}

/// <summary>
/// Filters of the caller's schedule query. <see cref="IncludeHosted"/> also lists occurrences
/// the caller hosts without holding a qualifying assignment (organizer-only).
/// </summary>
public sealed record PlayerOccurrenceQuery(
    DateTime? FromUtc,
    DateTime? ToUtc,
    bool IncludeTerminal,
    int PageSize,
    string? Cursor,
    bool IncludeHosted = false);

/// <summary>Body of <c>POST /api/v1/events/{occurrenceId}/host/transfer</c>.</summary>
public class TransferOccurrenceHostDto
{
    /// <summary>The existing, identity-linked player who becomes this occurrence's host.</summary>
    [Required]
    [NonEmptyGuid]
    public Guid? NewHostPlayerId { get; set; }
}

/// <summary>
/// One occurrence as a game page needs it (<c>GET /api/v1/events/{id}/detail</c>): when and
/// where, who hosts, lifecycle, the requirements with derived supply and readiness, the
/// current (live) participants, and the caller's own relationship. Only public player fields
/// appear (username, display name), never email or external identity data. Historical
/// (ended) assignments are not listed here; the paged roster assignments route serves history.
/// </summary>
public class OccurrenceDetailDto
{
    public Guid OccurrenceId { get; set; }
    public Guid? SeriesId { get; set; }

    /// <summary>The optional team association; null for pickup games.</summary>
    public Guid? TeamId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime ScheduledStartUtc { get; set; }
    public DateTime? ScheduledEndUtc { get; set; }
    public EventStatus Status { get; set; }

    /// <summary>
    /// Whether the occurrence still accepts roster changes (claims, leaves, host actions,
    /// host transfer). False once Completed, Cancelled or Archived.
    /// </summary>
    public bool AcceptsRosterChanges { get; set; }

    public string? Category { get; set; }
    public string? Region { get; set; }
    public Guid? VenueId { get; set; }

    /// <summary>The venue's name when a venue is attached, otherwise the free-text location.</summary>
    public string? Location { get; set; }

    /// <summary>
    /// The attached venue, if any, masked for this caller: a Private venue's street address and
    /// coordinates are shown only to the host and to current participants.
    /// </summary>
    public OccurrenceVenueDto? Venue { get; set; }

    public Guid? HostPlayerId { get; set; }
    public string? HostUsername { get; set; }
    public string? HostDisplayName { get; set; }

    /// <summary>Sum of RequiredCount over all requirements (0 when there are none).</summary>
    public int RequiredCount { get; set; }

    /// <summary>Supply-status assignments on the occurrence.</summary>
    public int SupplyCount { get; set; }

    /// <summary>Sum of per-requirement open quantities.</summary>
    public int OpenQuantity { get; set; }

    /// <summary>True only when at least one requirement exists and every one is filled.</summary>
    public bool IsRosterReady { get; set; }

    public IReadOnlyList<RosterRequirementDto> Requirements { get; set; } = [];

    /// <summary>Live (supply-status) participants in join order; bounded by the requirements' capacity.</summary>
    public IReadOnlyList<OccurrenceParticipantDto> Participants { get; set; } = [];

    /// <summary>Whether the caller is the host. False for an anonymous or unlinked caller.</summary>
    public bool IsHost { get; set; }

    /// <summary>The caller's live assignment, if any; null for an anonymous or unlinked caller.</summary>
    public Guid? MyAssignmentId { get; set; }

    public RosterAssignmentStatus? MyAssignmentStatus { get; set; }
    public Guid? MyRequirementId { get; set; }

    /// <summary>
    /// Requirements the caller asked to be notified about ("Notify me if a spot opens"). Only
    /// the caller's own subscriptions; empty for an anonymous or unlinked caller.
    /// </summary>
    public IReadOnlyList<Guid> MySubscribedRequirementIds { get; set; } = [];
}

/// <summary>A current participant on an occurrence detail: public player fields only.</summary>
public class OccurrenceParticipantDto
{
    public Guid AssignmentId { get; set; }
    public Guid PlayerId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public Guid? RequirementId { get; set; }
    public string? RoleCode { get; set; }
    public RosterAssignmentStatus Status { get; set; }

    /// <summary>Whether this participant is also the occurrence's host.</summary>
    public bool IsHost { get; set; }
}
