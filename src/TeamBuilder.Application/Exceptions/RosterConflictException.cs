namespace TeamBuilder.Application.Exceptions;

/// <summary>
/// An expected roster business conflict (409) carrying a stable machine-readable
/// <see cref="Code"/> from <see cref="RosterConflictCodes"/>. It is an
/// <see cref="InvalidOperationException"/>, so the global handler maps it to 409 and adds the
/// code to the ProblemDetails as <c>code</c>.
/// </summary>
public sealed class RosterConflictException : InvalidOperationException
{
    public RosterConflictException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>Stable roster conflict codes returned as ProblemDetails <c>code</c>.</summary>
public static class RosterConflictCodes
{
    /// <summary>The player already holds a live (supply) assignment for this occurrence.</summary>
    public const string AlreadyParticipating = "AlreadyParticipating";

    /// <summary>The requirement has no open quantity left.</summary>
    public const string RequirementFull = "RequirementFull";

    /// <summary>
    /// The requirement or assignment changed concurrently and nothing was saved. Retryable:
    /// capacity may still be open.
    /// </summary>
    public const string RosterChanged = "RosterChanged";

    /// <summary>The occurrence is Completed, Cancelled or Archived.</summary>
    public const string OccurrenceClosed = "OccurrenceClosed";

    /// <summary>The assignment no longer holds a roster spot (Departed, NoShow or Cancelled).</summary>
    public const string AssignmentEnded = "AssignmentEnded";

    /// <summary>The assignment named as replaced still holds a roster spot.</summary>
    public const string ReplacedAssignmentStillActive = "ReplacedAssignmentStillActive";

    /// <summary>The occurrence already has a requirement for this role code.</summary>
    public const string DuplicateRequirementRole = "DuplicateRequirementRole";
}
