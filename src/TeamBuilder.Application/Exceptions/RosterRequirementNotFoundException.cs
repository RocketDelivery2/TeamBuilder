namespace TeamBuilder.Application.Exceptions;

/// <summary>
/// Thrown when a self-claim names a roster requirement that does not exist on the target
/// occurrence (missing, or belonging to another occurrence). Mapped to 404 by the claim endpoint.
/// </summary>
public sealed class RosterRequirementNotFoundException : Exception
{
    public RosterRequirementNotFoundException(Guid requirementId)
        : base("Roster requirement not found.")
    {
        RequirementId = requirementId;
    }

    public Guid RequirementId { get; }
}
