namespace TeamBuilder.Domain.Enums;

/// <summary>
/// Outbox processing state. Persisted numerically and used in the outbox's filtered indexes;
/// never renumber. A message whose handler decided nothing was needed (the vacancy was already
/// refilled, the game closed) is Completed like any other: the outcome is logged and counted,
/// so a separate Skipped state would only duplicate that.
/// </summary>
public enum OutboxMessageStatus
{
    /// <summary>Waiting to be claimed (new, or a retry after a failed attempt).</summary>
    Pending = 1,

    /// <summary>Claimed by a worker under a lease.</summary>
    Processing = 2,

    /// <summary>Handled (including "nothing to do"). Terminal.</summary>
    Completed = 3,

    /// <summary>Gave up after the last attempt, or the message cannot be handled. Terminal.</summary>
    Failed = 4
}
