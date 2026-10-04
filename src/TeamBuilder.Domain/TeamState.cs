using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Domain;

/// <summary>
/// The single definition of a team's derived roster state. Capacity is never stored as a
/// status: it is always computed from CurrentMemberCount and MaxMembers.
/// </summary>
public static class TeamState
{
    public static bool IsFull(int currentMemberCount, int maxMembers) =>
        currentMemberCount >= maxMembers;

    public static int OpenSlots(int currentMemberCount, int maxMembers) =>
        Math.Max(0, maxMembers - currentMemberCount);

    public static bool HasVacancies(
        TeamLifecycleStatus lifecycleStatus,
        bool isAcceptingMembers,
        int currentMemberCount,
        int maxMembers) =>
        lifecycleStatus == TeamLifecycleStatus.Active
        && isAcceptingMembers
        && OpenSlots(currentMemberCount, maxMembers) > 0;

    /// <summary>Computes the legacy compatibility <see cref="TeamStatus"/>.</summary>
    public static TeamStatus ToLegacyStatus(
        TeamLifecycleStatus lifecycleStatus,
        bool isAcceptingMembers,
        int currentMemberCount,
        int maxMembers) =>
        lifecycleStatus switch
        {
            TeamLifecycleStatus.Inactive => TeamStatus.Inactive,
            TeamLifecycleStatus.Disbanded => TeamStatus.Disbanded,
            _ when IsFull(currentMemberCount, maxMembers) => TeamStatus.Full,
            _ when isAcceptingMembers => TeamStatus.Recruiting,
            _ => TeamStatus.Active
        };
}
