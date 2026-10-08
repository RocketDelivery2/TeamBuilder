using System.Diagnostics.CodeAnalysis;

namespace TeamBuilder.Domain;

/// <summary>
/// Discovery's activity filter. For now an activity is the occurrence's existing
/// <c>Category</c> (for example <c>basketball</c>), compared case-insensitively: there is no
/// sports taxonomy yet. Codes use the same TBRL <c>code</c> grammar as role codes
/// (<c>^[a-z0-9][a-z0-9._-]*$</c>), so a future TBRL activity code can replace Category without
/// changing the request contract.
/// </summary>
public static class ActivityCodes
{
    public const string Basketball = "basketball";

    /// <summary>The Category column's length.</summary>
    public const int MaxLength = 100;

    /// <summary>Trims and lowercases <paramref name="value"/>, then checks the code grammar.</summary>
    public static bool TryNormalize(
        string? value,
        [NotNullWhen(true)] out string? normalized,
        [NotNullWhen(false)] out string? error)
    {
        normalized = null;
        var candidate = value?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(candidate))
        {
            error = "activity must not be empty.";
            return false;
        }

        if (candidate.Length > MaxLength)
        {
            error = $"activity must be at most {MaxLength} characters.";
            return false;
        }

        if (!IsCodeStart(candidate[0]) || !candidate.All(c => IsCodeStart(c) || c is '.' or '_' or '-'))
        {
            error = "activity must start with a letter or digit and contain only letters, digits, '.', '_' or '-'.";
            return false;
        }

        normalized = candidate;
        error = null;
        return true;
    }

    private static bool IsCodeStart(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';
}
