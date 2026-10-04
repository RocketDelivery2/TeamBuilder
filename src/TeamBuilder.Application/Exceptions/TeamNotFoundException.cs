namespace TeamBuilder.Application.Exceptions;

/// <summary>
/// Thrown when an operation targets a team that does not exist.
/// Mapped to 404 by the join request creation endpoint only.
/// </summary>
public sealed class TeamNotFoundException : Exception
{
    public TeamNotFoundException(Guid teamId)
        : base("Team not found.")
    {
        TeamId = teamId;
    }

    public Guid TeamId { get; }
}
