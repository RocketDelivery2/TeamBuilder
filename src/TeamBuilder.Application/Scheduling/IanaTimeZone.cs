using System.Diagnostics.CodeAnalysis;

namespace TeamBuilder.Application.Scheduling;

/// <summary>
/// Validates product time zone identifiers using only built-in .NET APIs. An identifier is
/// accepted when it resolves through <see cref="TimeZoneInfo.FindSystemTimeZoneById"/>, is
/// known as an IANA identifier (<see cref="TimeZoneInfo.TryConvertIanaIdToWindowsId(string, out string?)"/>),
/// and resolves to a zone whose id is exactly the input (so a different-case spelling, which
/// some platforms would silently canonicalize, is rejected rather than rewritten). Windows ids
/// such as <c>Eastern Standard Time</c> are rejected even where the platform can resolve them.
/// </summary>
public static class IanaTimeZone
{
    public const int MaxLength = 100;

    public static bool TryResolve(
        string? id,
        [NotNullWhen(true)] out TimeZoneInfo? timeZone,
        [NotNullWhen(false)] out string? error)
    {
        timeZone = null;

        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxLength || id.Trim().Length != id.Length)
        {
            error = "TimeZoneId must be a non-empty IANA time zone identifier.";
            return false;
        }

        if (!TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _))
        {
            error = $"TimeZoneId '{id}' is not a supported IANA time zone identifier.";
            return false;
        }

        TimeZoneInfo resolved;
        try
        {
            resolved = TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            error = $"TimeZoneId '{id}' is not a supported IANA time zone identifier.";
            return false;
        }

        if (!string.Equals(resolved.Id, id, StringComparison.Ordinal))
        {
            error = $"TimeZoneId '{id}' is not a supported IANA time zone identifier.";
            return false;
        }

        timeZone = resolved;
        error = null;
        return true;
    }
}
