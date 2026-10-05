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
        DateOnly throughDate)
    {
        var last = seriesEndDate is { } end && end < throughDate ? end : throughDate;
        if (last < seriesStartDate)
            yield break;

        var index = 0;
        if (rule.Frequency == RecurrenceFrequency.Daily)
        {
            for (var date = seriesStartDate; date <= last; date = date.AddDays(rule.Interval))
            {
                yield return new RecurrenceDate(index++, date);
                if (date.DayNumber > DateOnly.MaxValue.DayNumber - rule.Interval)
                    yield break;
            }

            yield break;
        }

        var weekdays = rule.ByDay.Count > 0 ? rule.ByDay : [seriesStartDate.DayOfWeek];
        var weekStart = seriesStartDate.AddDays(-RecurrenceRuleParser.MondayFirst(seriesStartDate.DayOfWeek));
        while (weekStart <= last)
        {
            foreach (var day in weekdays)
            {
                var date = weekStart.AddDays(RecurrenceRuleParser.MondayFirst(day));
                if (date < seriesStartDate)
                    continue;
                if (date > last)
                    yield break;
                yield return new RecurrenceDate(index++, date);
            }

            if (weekStart.DayNumber > DateOnly.MaxValue.DayNumber - 7 * rule.Interval)
                yield break;
            weekStart = weekStart.AddDays(7 * rule.Interval);
        }
    }
}
