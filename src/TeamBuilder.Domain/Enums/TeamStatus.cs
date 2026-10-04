namespace TeamBuilder.Domain.Enums;

/// <summary>
/// Legacy API compatibility status. It is never persisted: it is computed from
/// <see cref="TeamLifecycleStatus"/>, <c>IsAcceptingMembers</c> and roster capacity by
/// <see cref="TeamBuilder.Domain.TeamState.ToLegacyStatus"/>. Deprecated in favour of
/// <c>LifecycleStatus</c>, <c>IsAcceptingMembers</c>, <c>IsFull</c> and <c>HasVacancies</c>.
/// </summary>
public enum TeamStatus
{
    Active = 1,
    Recruiting = 2,
    Full = 3,
    Inactive = 4,
    Disbanded = 5
}
