namespace TeamBuilder.Domain.Enums;

/// <summary>
/// A host-controlled day-of-game step on a live roster assignment. Each one is a narrow action
/// with a fixed set of allowed source statuses (see <see cref="RosterState.TargetStatusFor"/>);
/// there is no arbitrary status mutation.
/// </summary>
public enum RosterAssignmentTransition
{
    /// <summary>Confirmed to CheckedIn: the player arrived.</summary>
    CheckIn = 1,

    /// <summary>CheckedIn to Active: the player is in the game.</summary>
    Activate = 2,

    /// <summary>Confirmed or CheckedIn to NoShow: the player never played. Releases the spot.</summary>
    NoShow = 3
}
