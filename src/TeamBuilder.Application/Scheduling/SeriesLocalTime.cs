namespace TeamBuilder.Application.Scheduling;

/// <summary>
/// Converts a series' local wall-clock intent (date + time in its IANA zone) to a UTC instant.
/// Recurring events keep their local time across DST, so each date is converted on its own.
/// <list type="bullet">
/// <item>Spring-forward gap (the local time does not exist): the occurrence moves to the first
/// valid local instant after the gap, e.g. 02:30 on a US spring-forward day becomes 03:00 local.</item>
/// <item>Fall-back overlap (the local time happens twice): the standard-time instant is chosen,
/// e.g. 01:30 on a US fall-back day is 01:30 standard time (the later of the two).</item>
/// </list>
/// </summary>
public static class SeriesLocalTime
{
    /// <summary>Returns the UTC instant (<see cref="DateTimeKind.Utc"/>) for the local date and time.</summary>
    public static DateTime ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);

        if (timeZone.IsInvalidTime(local))
        {
            // Gaps start on a whole minute, so stepping by minutes from the truncated local time
            // lands exactly on the gap's end. Real gaps are at most a few hours; the bound only
            // guards against a malformed zone.
            var candidate = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified);
            for (var i = 0; i < 24 * 60 && timeZone.IsInvalidTime(candidate); i++)
                candidate = candidate.AddMinutes(1);

            if (timeZone.IsInvalidTime(candidate))
                throw new InvalidOperationException($"Could not resolve a valid local time in '{timeZone.Id}' for {local:O}.");

            local = candidate;
        }

        if (timeZone.IsAmbiguousTime(local))
        {
            var offsets = timeZone.GetAmbiguousTimeOffsets(local);
            var standard = offsets
                .Select(offset => new DateTimeOffset(local, offset))
                .Where(instant => !timeZone.IsDaylightSavingTime(instant))
                .Select(instant => (TimeSpan?)instant.Offset)
                .FirstOrDefault() ?? offsets.Min();

            return DateTime.SpecifyKind(local - standard, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
    }

    /// <summary>The current calendar date in <paramref name="timeZone"/>.</summary>
    public static DateOnly Today(TimeProvider timeProvider, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timeZone).DateTime);
}
