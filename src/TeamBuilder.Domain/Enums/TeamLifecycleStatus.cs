namespace TeamBuilder.Domain.Enums;

/// <summary>
/// The administrative lifecycle of a team. Recruitment (<c>Team.IsAcceptingMembers</c>) and
/// physical capacity (<c>CurrentMemberCount</c>/<c>MaxMembers</c>) are separate facts.
/// </summary>
public enum TeamLifecycleStatus
{
    Active = 1,
    Inactive = 2,
    Disbanded = 3
}
