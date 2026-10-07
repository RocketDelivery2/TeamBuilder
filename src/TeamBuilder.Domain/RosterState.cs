using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Domain;

/// <summary>
/// Central roster arithmetic. Supply = assignments in a supply status (Reserved, Confirmed,
/// CheckedIn, Active); open quantity = max(0, RequiredCount - SupplyCount). Open quantity and
/// readiness are derived only; nothing here is persisted. Every roster feature computes supply
/// through <see cref="Compute"/> rather than re-implementing it.
/// </summary>
public static class RosterState
{
    /// <summary>The statuses that hold roster supply, in persisted numeric order.</summary>
    public static readonly IReadOnlyList<RosterAssignmentStatus> SupplyStatuses =
    [
        RosterAssignmentStatus.Reserved,
        RosterAssignmentStatus.Confirmed,
        RosterAssignmentStatus.CheckedIn,
        RosterAssignmentStatus.Active
    ];

    public static bool IsSupply(RosterAssignmentStatus status) =>
        status is RosterAssignmentStatus.Reserved
            or RosterAssignmentStatus.Confirmed
            or RosterAssignmentStatus.CheckedIn
            or RosterAssignmentStatus.Active;

    /// <summary>
    /// Whether new requirements/assignments may be added to an occurrence in
    /// <paramref name="status"/>. Completed, Cancelled and Archived occurrences are closed;
    /// Planned, Open and InProgress are not (in-progress events still take last-minute subs).
    /// </summary>
    public static bool AcceptsNewRosterMutations(EventStatus status) =>
        status is not (EventStatus.Completed or EventStatus.Cancelled or EventStatus.Archived);

    /// <summary>
    /// The terminal status a live assignment moves to when it ends (player left, host removed):
    /// Reserved/Confirmed never participated, so they become Cancelled; CheckedIn/Active did, so
    /// they become Departed. Either way the row stops holding supply.
    /// </summary>
    public static RosterAssignmentStatus ExitStatusFor(RosterAssignmentStatus status) => status switch
    {
        RosterAssignmentStatus.Reserved or RosterAssignmentStatus.Confirmed => RosterAssignmentStatus.Cancelled,
        RosterAssignmentStatus.CheckedIn or RosterAssignmentStatus.Active => RosterAssignmentStatus.Departed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Only a supply status can end.")
    };

    /// <summary>
    /// The status a host lifecycle <paramref name="transition"/> moves an assignment in
    /// <paramref name="current"/> to, or null when the transition is not allowed from it:
    /// <list type="bullet">
    /// <item>CheckIn: Confirmed to CheckedIn;</item>
    /// <item>Activate: CheckedIn to Active;</item>
    /// <item>NoShow: Confirmed or CheckedIn to NoShow.</item>
    /// </list>
    /// Reserved is not a source of any transition, and a non-supply (terminal) status never is,
    /// so ended rows are never resurrected. CheckedIn and Active hold supply; NoShow does not.
    /// </summary>
    public static RosterAssignmentStatus? TargetStatusFor(RosterAssignmentStatus current, RosterAssignmentTransition transition) =>
        (transition, current) switch
        {
            (RosterAssignmentTransition.CheckIn, RosterAssignmentStatus.Confirmed) => RosterAssignmentStatus.CheckedIn,
            (RosterAssignmentTransition.Activate, RosterAssignmentStatus.CheckedIn) => RosterAssignmentStatus.Active,
            (RosterAssignmentTransition.NoShow, RosterAssignmentStatus.Confirmed or RosterAssignmentStatus.CheckedIn) => RosterAssignmentStatus.NoShow,
            _ => null
        };

    public static int OpenQuantity(int requiredCount, int supplyCount) =>
        Math.Max(0, requiredCount - supplyCount);

    /// <summary>
    /// The single roster-supply computation for one occurrence, from its requirements and the
    /// (RequirementId, Status) pairs of its assignments (any statuses; non-supply rows are
    /// ignored). Assignments linked to no requirement count toward
    /// <see cref="RosterSupplySnapshot.TotalSupplyCount"/> and
    /// <see cref="RosterSupplySnapshot.UnlinkedSupplyCount"/>, never toward a requirement.
    /// </summary>
    public static RosterSupplySnapshot Compute(
        Guid occurrenceId,
        IEnumerable<(Guid RequirementId, int RequiredCount)> requirements,
        IEnumerable<(Guid? RequirementId, RosterAssignmentStatus Status)> assignments)
    {
        var supplyByRequirement = new Dictionary<Guid, int>();
        var totalSupply = 0;
        var unlinkedSupply = 0;

        foreach (var (requirementId, status) in assignments)
        {
            if (!IsSupply(status))
                continue;

            totalSupply++;
            if (requirementId is { } id)
                supplyByRequirement[id] = supplyByRequirement.GetValueOrDefault(id) + 1;
            else
                unlinkedSupply++;
        }

        var requirementStates = requirements
            .Select(r =>
            {
                var supply = supplyByRequirement.GetValueOrDefault(r.RequirementId);
                return new RequirementSupply(r.RequirementId, r.RequiredCount, supply, OpenQuantity(r.RequiredCount, supply));
            })
            .ToList();

        return new RosterSupplySnapshot(
            occurrenceId,
            TotalSupplyCount: totalSupply,
            TotalRequiredCount: requirementStates.Sum(r => r.RequiredCount),
            TotalOpenQuantity: requirementStates.Sum(r => r.OpenQuantity),
            // Readiness is never invented: with no requirements there is no stated demand, so
            // the roster is not ready. MaxParticipants is never consulted.
            IsRosterReady: requirementStates.Count > 0 && requirementStates.All(r => r.OpenQuantity == 0),
            UnlinkedSupplyCount: unlinkedSupply,
            Requirements: requirementStates.ToDictionary(r => r.RequirementId));
    }
}

public sealed record RequirementSupply(Guid RequirementId, int RequiredCount, int SupplyCount, int OpenQuantity);

/// <summary>
/// Authoritative roster supply of one occurrence. <see cref="TotalOpenQuantity"/> sums the
/// per-requirement open quantities, so surplus on one requirement never offsets another.
/// <see cref="IsRosterReady"/> is true only when at least one requirement exists and every
/// requirement has zero open quantity.
/// </summary>
public sealed record RosterSupplySnapshot(
    Guid OccurrenceId,
    int TotalSupplyCount,
    int TotalRequiredCount,
    int TotalOpenQuantity,
    bool IsRosterReady,
    int UnlinkedSupplyCount,
    IReadOnlyDictionary<Guid, RequirementSupply> Requirements);
