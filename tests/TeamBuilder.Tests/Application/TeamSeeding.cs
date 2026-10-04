using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Test seeding helpers for the lifecycle/recruitment model.
/// </summary>
internal static class TeamSeeding
{
    /// <summary>
    /// Maps a legacy <see cref="TeamStatus"/> to seed values using the Director's migration
    /// backfill mapping (Active = recruitment paused; Recruiting/Full = accepting). Capacity
    /// itself still comes from CurrentMemberCount/MaxMembers.
    /// </summary>
    public static TeamLifecycleStatus Lifecycle(TeamStatus status) => FromLegacy(status).LifecycleStatus;

    public static bool Accepting(TeamStatus status) => FromLegacy(status).IsAcceptingMembers;

    public static (TeamLifecycleStatus LifecycleStatus, bool IsAcceptingMembers) FromLegacy(TeamStatus status) =>
        status switch
        {
            TeamStatus.Active => (TeamLifecycleStatus.Active, false),
            TeamStatus.Recruiting => (TeamLifecycleStatus.Active, true),
            TeamStatus.Full => (TeamLifecycleStatus.Active, true),
            TeamStatus.Inactive => (TeamLifecycleStatus.Inactive, false),
            TeamStatus.Disbanded => (TeamLifecycleStatus.Disbanded, false),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };
}
