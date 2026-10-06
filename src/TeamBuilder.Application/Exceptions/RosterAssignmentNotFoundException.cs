namespace TeamBuilder.Application.Exceptions;

/// <summary>
/// Thrown when a roster transition names an assignment that does not exist on the target
/// occurrence (missing, or belonging to another occurrence). Mapped to 404 by the roster endpoints.
/// </summary>
public sealed class RosterAssignmentNotFoundException : Exception
{
    public RosterAssignmentNotFoundException(Guid assignmentId)
        : base("Roster assignment not found.")
    {
        AssignmentId = assignmentId;
    }

    public Guid AssignmentId { get; }
}
