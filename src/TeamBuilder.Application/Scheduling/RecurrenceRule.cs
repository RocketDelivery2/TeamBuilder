namespace TeamBuilder.Application.Scheduling;

public enum RecurrenceFrequency
{
    Daily = 1,
    Weekly = 2
}

/// <summary>
/// A parsed RRULE in the supported V0.1 subset. <see cref="ByDay"/> is empty when the rule
/// had no BYDAY component; it is sorted Monday-first and never contains duplicates.
/// </summary>
public sealed record RecurrenceRule(RecurrenceFrequency Frequency, int Interval, IReadOnlyList<DayOfWeek> ByDay);
