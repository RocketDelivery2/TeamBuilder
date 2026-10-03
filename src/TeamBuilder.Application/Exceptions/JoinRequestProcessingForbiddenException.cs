namespace TeamBuilder.Application.Exceptions;

/// <summary>
/// Thrown when an authenticated caller attempts to process a join request for a team they do not own.
/// Mapped to 403 by the join request processing endpoint only.
/// </summary>
public sealed class JoinRequestProcessingForbiddenException : Exception
{
    public JoinRequestProcessingForbiddenException(Guid joinRequestId, Guid callerUserId)
        : base("Only the team owner can process this join request.")
    {
        JoinRequestId = joinRequestId;
        CallerUserId = callerUserId;
    }

    public Guid JoinRequestId { get; }

    public Guid CallerUserId { get; }
}
