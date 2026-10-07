namespace TeamBuilder.Application.Exceptions;

/// <summary>
/// An invalid roster request (400) carrying a stable machine-readable <see cref="Code"/> from
/// <see cref="RosterValidationCodes"/>. It is an <see cref="ArgumentException"/>, so the global
/// handler maps it to 400 and adds the code to the ProblemDetails as <c>code</c>.
/// </summary>
public sealed class RosterValidationException : ArgumentException
{
    public RosterValidationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>Stable roster validation codes returned as ProblemDetails <c>code</c> on a 400.</summary>
public static class RosterValidationCodes
{
    /// <summary>
    /// A capacity-consuming host assignment named no roster requirement. Every public
    /// assignment fills a requirement of its occurrence, so it counts toward readiness.
    /// </summary>
    public const string RequirementIdRequired = "RequirementIdRequired";

    /// <summary>The named requirement does not exist or belongs to another occurrence.</summary>
    public const string RequirementNotOnOccurrence = "RequirementNotOnOccurrence";

    /// <summary>The same role code appears more than once in one create request.</summary>
    public const string DuplicateRequirementRoleInRequest = "DuplicateRequirementRoleInRequest";

    /// <summary><c>hostParticipates</c> is true but the request creates no roster requirement.</summary>
    public const string HostParticipationRequiresRequirement = "HostParticipationRequiresRequirement";

    /// <summary>
    /// <c>hostParticipates</c> is true, several requirements are created, and
    /// <c>hostRoleCode</c> does not name exactly one of them.
    /// </summary>
    public const string HostRoleCodeInvalid = "HostRoleCodeInvalid";
}
