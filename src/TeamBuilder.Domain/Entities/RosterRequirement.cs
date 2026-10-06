namespace TeamBuilder.Domain.Entities;

/// <summary>
/// Quantity-based roster demand for one <see cref="EventOccurrence"/>: "this occurrence needs
/// <see cref="RequiredCount"/> players in role <see cref="RoleCode"/>". There are no numbered
/// slots. Generic, role-less demand uses the canonical role code
/// <see cref="RosterRoleCodes.Participant"/>, so <see cref="RoleCode"/> is never null and is
/// unique within an occurrence.
/// </summary>
public class RosterRequirement : BaseEntity
{
    public Guid OccurrenceId { get; set; }
    public EventOccurrence EventOccurrence { get; set; } = null!;

    /// <summary>
    /// Canonical TeamBuilder role code, interpreted within the occurrence's activity
    /// (e.g. <c>participant</c>, <c>guard</c>, <c>tank</c>). Normalized lowercase; see
    /// <see cref="RosterRoleCodes"/>.
    /// </summary>
    public string RoleCode { get; set; } = RosterRoleCodes.Participant;

    /// <summary>Human-readable position name, e.g. "Point Guard".</summary>
    public string? DisplayPosition { get; set; }

    /// <summary>The imported/provider term this requirement was mapped from, preserved verbatim.</summary>
    public string? SourceRoleLabel { get; set; }

    public int RequiredCount { get; set; }
}
