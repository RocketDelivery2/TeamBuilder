namespace TeamBuilder.Application.Exceptions;

/// <summary>
/// Thrown when a player tries to leave on behalf of an assignment they do not hold. Mapped to
/// 403 by the self-leave endpoint.
/// </summary>
public sealed class RosterAssignmentForbiddenException : Exception
{
    public RosterAssignmentForbiddenException(Guid assignmentId, Guid callerPlayerId)
        : base("Only the player holding this roster assignment can leave it.")
    {
        AssignmentId = assignmentId;
        CallerPlayerId = callerPlayerId;
    }

    public Guid AssignmentId { get; }

    public Guid CallerPlayerId { get; }
}
