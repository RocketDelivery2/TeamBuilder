namespace TeamBuilder.Application.Scheduling;

/// <summary>A recurrence date and its zero-based position from the start of the series.</summary>
public readonly record struct RecurrenceDate(int Index, DateOnly Date);

/// <summary>
/// Expands a <see cref="RecurrenceRule"/> into local calendar dates. The series start date plays
/// the role of DTSTART: it is the first candidate date, and indices count every recurrence date
/// from it, so an index is stable no matter which window is being materialized.
/// <list type="bullet">
/// <item>DAILY: the start date, then every INTERVAL days.</item>
/// <item>WEEKLY: weeks run Monday..Sunday (WKST=MO); the week containing the start date is week 0
/// and every INTERVAL-th week is active. Active weeks yield their BYDAY weekdays (or the start
/// date's weekday when BYDAY is omitted), never before the start date.</item>
/// </list>
/// </summary>
public static class RecurrenceSchedule
{
    /// <summary>
    /// Recurrence dates from <paramref name="seriesStartDate"/> through the earlier of
    /// <paramref name="seriesEndDate"/> and <paramref name="throughDate"/>, inclusive, in order.
    /// </summary>
    public static IEnumerable<RecurrenceDate> Enumerate(
        RecurrenceRule rule,
        DateOnly seriesStartDate,
        DateOnly? seriesEndDate,
        DateOnly throughDate) =>
        Enumerate(rule, seriesStartDate, seriesEndDate, seriesStartDate, throughDate);

    /// <summary>
    /// Recurrence dates on or after <paramref name="fromDate"/> and on or before the earlier of
    /// <paramref name="seriesEndDate"/> and <paramref name="throughDate"/>, in order. Each index is
    /// the one full enumeration from <paramref name="seriesStartDate"/> would assign, but it is
    /// computed arithmetically: the work is proportional to the requested range, not to the age
    /// of the series.
    /// </summary>
    public static IEnumerable<RecurrenceDate> Enumerate(
        RecurrenceRule rule,
        DateOnly seriesStartDate,
        DateOnly? seriesEndDate,
        DateOnly fromDate,
        DateOnly throughDate)
    {
        var first = fromDate > seriesStartDate ? fromDate : seriesStartDate;
        var last = seriesEndDate is { } end && end < throughDate ? end : throughDate;
        if (last < first)
            return [];

        return rule.Frequency == RecurrenceFrequency.Daily
            ? EnumerateDaily(rule.Interval, seriesStartDate, first, last)
            : EnumerateWeekly(rule, seriesStartDate, first, last);
    }

    private static IEnumerable<RecurrenceDate> EnumerateDaily(int interval, DateOnly seriesStartDate, DateOnly first, DateOnly last)
    {
        // The k-th date is start + k·INTERVAL; begin at the smallest k whose date is >= first.
        long k = CeilingDivide(first.DayNumber - seriesStartDate.DayNumber, interval);
        for (long dayNumber = seriesStartDate.DayNumber + k * interval; dayNumber <= last.DayNumber; dayNumber += interval, k++)
            yield return new RecurrenceDate(checked((int)k), DateOnly.FromDayNumber((int)dayNumber));
    }

    private static IEnumerable<RecurrenceDate> EnumerateWeekly(RecurrenceRule rule, DateOnly seriesStartDate, DateOnly first, DateOnly last)
    {
        var weekdays = rule.ByDay.Count > 0 ? rule.ByDay : [seriesStartDate.DayOfWeek];
        var perWeek = weekdays.Count;
        var startOffset = RecurrenceRuleParser.MondayFirst(seriesStartDate.DayOfWeek);
        var startWeek = seriesStartDate.DayNumber - startOffset;

        // Week 0 only yields the weekdays on or after the start date; every later active week
        // yields all of them.
        var firstWeekCount = weekdays.Count(day => RecurrenceRuleParser.MondayFirst(day) >= startOffset);

        // The first active week (counted in active weeks, m) that can contain a date >= first.
        var weeksToFirst = (first.DayNumber - RecurrenceRuleParser.MondayFirst(first.DayOfWeek) - startWeek) / 7;
        long m = CeilingDivide(weeksToFirst, rule.Interval);
        long index = m == 0 ? 0 : firstWeekCount + (m - 1) * perWeek;

        for (long weekStart = startWeek + m * 7 * rule.Interval; weekStart <= last.DayNumber; weekStart += 7L * rule.Interval)
        {
            foreach (var day in weekdays)
            {
                var dayNumber = weekStart + RecurrenceRuleParser.MondayFirst(day);
                if (dayNumber < seriesStartDate.DayNumber)
                    continue;
                if (dayNumber > last.DayNumber)
                    yield break;

                var currentIndex = index++;
                if (dayNumber >= first.DayNumber)
                    yield return new RecurrenceDate(checked((int)currentIndex), DateOnly.FromDayNumber((int)dayNumber));
            }
        }
    }

    private static long CeilingDivide(long value, long divisor) => value <= 0 ? 0 : (value + divisor - 1) / divisor;
}
