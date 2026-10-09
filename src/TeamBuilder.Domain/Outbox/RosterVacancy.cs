using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Domain.Outbox;

/// <summary>Why capacity opened, as published in <c>roster.vacancy.opened.v1</c>.</summary>
public enum VacancyReason
{
    PlayerLeft = 1,
    HostRemoved = 2,
    NoShow = 3
}

/// <summary>
/// The vacancy emission rule. One assignment of a requirement stops holding supply:
/// <code>
/// beforeSupply = otherSupply + 1      beforeOpen = max(0, RequiredCount - beforeSupply)
/// afterSupply  = otherSupply          afterOpen  = max(0, RequiredCount - afterSupply)
/// </code>
/// A vacancy opened only when <c>afterOpen &gt; beforeOpen</c>. On an overfilled requirement
/// (more supply than required, e.g. legacy data) a departure may open nothing.
/// </summary>
public static class RosterVacancy
{
    public readonly record struct Change(int PreviousOpenQuantity, int OpenQuantity)
    {
        public bool Opened => OpenQuantity > PreviousOpenQuantity;
    }

    public static Change Evaluate(int requiredCount, int otherSupply)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(otherSupply);
        return new Change(
            RosterState.OpenQuantity(requiredCount, otherSupply + 1),
            RosterState.OpenQuantity(requiredCount, otherSupply));
    }

    /// <summary>
    /// The published reason for an exit, or null for an exit that is not a current vacancy
    /// path (Replaced/Other are not produced by any roster mutation today).
    /// </summary>
    public static VacancyReason? ReasonFor(RosterExitReason? exitReason) => exitReason switch
    {
        RosterExitReason.PlayerLeft => VacancyReason.PlayerLeft,
        RosterExitReason.HostRemoved => VacancyReason.HostRemoved,
        RosterExitReason.NoShow => VacancyReason.NoShow,
        _ => null
    };
}
