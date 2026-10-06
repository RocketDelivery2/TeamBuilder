using System.Diagnostics.CodeAnalysis;

namespace TeamBuilder.Domain;

/// <summary>
/// Canonical role codes for roster requirements and assignments. A code is interpreted within
/// its activity (TBRL v0.1 "Activity-specific roles"), never as a universal enum, so no sport
/// or game roles are hard-coded here. Codes follow the TBRL <c>code</c> grammar
/// <c>^[a-z0-9][a-z0-9._-]*$</c>.
/// </summary>
public static class RosterRoleCodes
{
    /// <summary>Generic, role-less demand ("any player").</summary>
    public const string Participant = "participant";

    public const int MaxLength = 100;

    /// <summary>
    /// Trims and lowercases <paramref name="value"/> and checks it against the TBRL code
    /// grammar. Returns false (with an error) for empty, too long or malformed codes.
    /// </summary>
    public static bool TryNormalize(
        string? value,
        [NotNullWhen(true)] out string? normalized,
        [NotNullWhen(false)] out string? error)
    {
        normalized = null;
        var candidate = value?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(candidate))
        {
            error = "RoleCode must not be empty.";
            return false;
        }

        if (candidate.Length > MaxLength)
        {
            error = $"RoleCode must be at most {MaxLength} characters.";
            return false;
        }

        if (!IsCodeStart(candidate[0]) || !candidate.All(c => IsCodeStart(c) || c is '.' or '_' or '-'))
        {
            error = "RoleCode must start with a lowercase letter or digit and contain only lowercase letters, digits, '.', '_' or '-'.";
            return false;
        }

        normalized = candidate;
        error = null;
        return true;
    }

    private static bool IsCodeStart(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';
}
