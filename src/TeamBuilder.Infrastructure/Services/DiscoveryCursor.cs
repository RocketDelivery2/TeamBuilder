using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TeamBuilder.Application.DTOs;

namespace TeamBuilder.Infrastructure.Services;

/// <summary>
/// Keyset position in a discovery result: the exact (DistanceMeters, ScheduledStartUtc,
/// OccurrenceId) of the last item of a page, the same key the results are ordered by. The
/// distance is carried as the IEEE-754 bits SQL Server returned, never as a rounded display
/// value, so equal and near-equal distances continue deterministically. It also carries the
/// search window, so a search whose window defaulted to "from now" keeps the same window on
/// later pages. It is bound to its search by a short hash of the search parameters, so reusing
/// it with a different point, radius, activity, window or filter is a 400 rather than a
/// silently wrong page. The hash is one-way and the cursor holds no coordinates.
/// </summary>
internal readonly record struct DiscoveryCursor(
    double DistanceMeters,
    DateTime ScheduledStartUtc,
    Guid OccurrenceId,
    DateTime FromUtc,
    DateTime ToUtc,
    string SearchHash)
{
    private const string Version = "d1";

    public string Encode()
    {
        var raw = string.Join(':',
            Version,
            BitConverter.DoubleToInt64Bits(DistanceMeters).ToString("x16", CultureInfo.InvariantCulture),
            ScheduledStartUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            OccurrenceId.ToString("N"),
            FromUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            ToUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            SearchHash);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>Decodes a cursor; null when it was not produced by <see cref="Encode"/>.</summary>
    public static DiscoveryCursor? TryParse(string cursor)
    {
        try
        {
            if (cursor.Length > 512)
                return null;
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(base64)).Split(':');
            if (parts.Length == 7 &&
                parts[0] == Version &&
                long.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bits) &&
                BitConverter.Int64BitsToDouble(bits) is var distance && double.IsFinite(distance) && distance >= 0 &&
                TryTicks(parts[2], out var start) &&
                Guid.TryParseExact(parts[3], "N", out var occurrenceId) &&
                TryTicks(parts[4], out var from) &&
                TryTicks(parts[5], out var to))
            {
                return new DiscoveryCursor(distance, start, occurrenceId, from, to, parts[6]);
            }
        }
        catch (FormatException)
        {
        }

        return null;
    }

    private static bool TryTicks(string text, out DateTime value)
    {
        value = default;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            return false;
        value = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    /// <summary>A short one-way digest of everything that defines the result set.</summary>
    public static string HashOf(DiscoverOccurrencesQuery query, DateTime fromUtc, DateTime toUtc)
    {
        var canonical = string.Join('|',
            query.Latitude.ToString("R", CultureInfo.InvariantCulture),
            query.Longitude.ToString("R", CultureInfo.InvariantCulture),
            query.RadiusMiles.ToString("R", CultureInfo.InvariantCulture),
            query.Activity ?? string.Empty,
            fromUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            toUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            query.OpenOnly ? "1" : "0");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
    }
}
