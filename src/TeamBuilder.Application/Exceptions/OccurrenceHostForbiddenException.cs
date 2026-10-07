namespace TeamBuilder.Application.Exceptions;

/// <summary>
/// Thrown when a host-only occurrence action is attempted by a player who is not the
/// occurrence's current host. Mapped to 403. Host authority is re-checked inside the same
/// commit as the change, so a host who has just transferred the event away is refused.
/// </summary>
public sealed class OccurrenceHostForbiddenException : Exception
{
    public OccurrenceHostForbiddenException(Guid occurrenceId, Guid callerPlayerId)
        : base("Only the event's host can do this.")
    {
        OccurrenceId = occurrenceId;
        CallerPlayerId = callerPlayerId;
    }

    public Guid OccurrenceId { get; }

    public Guid CallerPlayerId { get; }
}
