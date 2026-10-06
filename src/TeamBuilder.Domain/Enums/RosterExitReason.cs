namespace TeamBuilder.Domain.Enums;

/// <summary>Why an assignment stopped holding roster supply.</summary>
public enum RosterExitReason
{
    PlayerLeft = 1,
    HostRemoved = 2,
    NoShow = 3,
    Replaced = 4,
    Other = 5
}
