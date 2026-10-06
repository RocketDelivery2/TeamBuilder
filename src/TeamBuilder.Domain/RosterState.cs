using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Domain;

/// <summary>
/// Central roster arithmetic. Supply = assignments in a supply status (Reserved, Confirmed,
/// CheckedIn, Active); open quantity = max(0, RequiredCount - SupplyCount). Open quantity is
/// derived only; nothing here is persisted.
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

    public static int OpenQuantity(int requiredCount, int supplyCount) =>
        Math.Max(0, requiredCount - supplyCount);

    /// <summary>
    /// Computes supply for one occurrence from its requirements and the
    /// (RequirementId, Status) pairs of its assignments. Assignments linked to no requirement
    /// count toward the occurrence total and <see cref="RosterSnapshot.UnlinkedSupplyCount"/>,
    /// never toward a requirement.
    /// </summary>
    public static RosterSnapshot Compute(
        IEnumerable<(Guid RequirementId, int RequiredCount)> requirements,
        IEnumerable<(Guid? RequirementId, RosterAssignmentStatus Status)> assignments)
    {
        var supplyByRequirement = new Dictionary<Guid, int>();
        var occurrenceSupply = 0;
        var unlinkedSupply = 0;

        foreach (var (requirementId, status) in assignments)
        {
            if (!IsSupply(status))
                continue;

            occurrenceSupply++;
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
            .ToDictionary(r => r.RequirementId);

        return new RosterSnapshot(occurrenceSupply, unlinkedSupply, requirementStates);
    }
}

public sealed record RequirementSupply(Guid RequirementId, int RequiredCount, int SupplyCount, int OpenQuantity);

public sealed record RosterSnapshot(
    int SupplyCount,
    int UnlinkedSupplyCount,
    IReadOnlyDictionary<Guid, RequirementSupply> Requirements);
