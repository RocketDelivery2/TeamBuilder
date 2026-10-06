using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Domain.Entities;

/// <summary>
/// Live TeamBuilder participation state of one <see cref="Player"/> for one
/// <see cref="EventOccurrence"/>. Rows are historical: a departure or no-show is recorded on
/// the row, and a replacement is a new row pointing back through
/// <see cref="ReplacedAssignmentId"/>; an old row is never reused.
/// </summary>
/// <remarks>
/// Participation is independent of team membership: any Player can hold an assignment, with no
/// <see cref="TeamMember"/> required. Contrast <see cref="RosterEntry"/>, which is imported/raw
/// roster provenance and never live participation.
/// </remarks>
public class RosterAssignment : BaseEntity
{
    public Guid OccurrenceId { get; set; }
    public EventOccurrence EventOccurrence { get; set; } = null!;

    public Guid PlayerId { get; set; }
    public Player Player { get; set; } = null!;

    /// <summary>
    /// The requirement this assignment fills, when known. Null for legacy/imported participants
    /// that do not map to a formal requirement yet. When set it must belong to the same
    /// occurrence (enforced by a composite foreign key).
    /// </summary>
    public Guid? RequirementId { get; set; }
    public RosterRequirement? Requirement { get; set; }

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

    /// <summary>
    /// The earlier assignment of the same occurrence that this one replaces (typically a
    /// departed or no-show assignment). Never this row itself.
    /// </summary>
    public Guid? ReplacedAssignmentId { get; set; }
    public RosterAssignment? ReplacedAssignment { get; set; }
}
