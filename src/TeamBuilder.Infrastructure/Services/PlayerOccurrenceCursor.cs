using System.Globalization;
using System.Text;

namespace TeamBuilder.Infrastructure.Services;

/// <summary>
/// Keyset position in the caller's schedule: the (ScheduledStartUtc, OccurrenceId) of the last
/// item of a page, the same key the schedule is ordered by. Opaque to clients (base64url text).
/// Keyset paging stays deterministic while occurrences are added or move in time, unlike
/// offset paging.
/// </summary>
internal readonly record struct PlayerOccurrenceCursor(DateTime ScheduledStartUtc, Guid OccurrenceId)
{
    public string Encode()
    {
        var raw = $"{ScheduledStartUtc.Ticks.ToString(CultureInfo.InvariantCulture)}:{OccurrenceId:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <exception cref="ArgumentException">The cursor was not produced by <see cref="Encode"/>.</exception>
    public static PlayerOccurrenceCursor Parse(string cursor)
    {
        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(base64)).Split(':');
            if (parts.Length == 2 &&
                long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) &&
                ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks &&
                Guid.TryParseExact(parts[1], "N", out var occurrenceId))
            {
                return new PlayerOccurrenceCursor(new DateTime(ticks, DateTimeKind.Utc), occurrenceId);
            }
        }
        catch (FormatException)
        {
        }

        throw new ArgumentException("The cursor is not valid.");
    }
}
