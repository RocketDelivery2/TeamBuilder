using System.Diagnostics.CodeAnalysis;

namespace TeamBuilder.Application.Scheduling;

/// <summary>
/// Strict parser for the V0.1 RRULE subset. The stored value stays an RFC 5545 RRULE value
/// (for example <c>FREQ=WEEKLY;BYDAY=TU</c>), but only these components are accepted:
/// <list type="bullet">
/// <item><c>FREQ</c> (required): <c>DAILY</c> or <c>WEEKLY</c>.</item>
/// <item><c>INTERVAL</c> (optional, default 1): an integer 1..52.</item>
/// <item><c>BYDAY</c> (optional, WEEKLY only): comma-separated <c>MO</c>..<c>SU</c>, no ordinals.</item>
/// </list>
/// Everything else is rejected, including COUNT and UNTIL (the series end date bounds the
/// recurrence), BYHOUR/BYMINUTE/BYSECOND/BYMONTH/BYMONTHDAY/BYSETPOS, unknown keys, duplicate
/// keys and duplicate weekdays. Keys and values are matched case-sensitively in upper case.
/// </summary>
public static class RecurrenceRuleParser
{
    public const int MinInterval = 1;
    public const int MaxInterval = 52;

    private static readonly Dictionary<string, DayOfWeek> WeekdayCodes = new(StringComparer.Ordinal)
    {
        ["MO"] = DayOfWeek.Monday,
        ["TU"] = DayOfWeek.Tuesday,
        ["WE"] = DayOfWeek.Wednesday,
        ["TH"] = DayOfWeek.Thursday,
        ["FR"] = DayOfWeek.Friday,
        ["SA"] = DayOfWeek.Saturday,
        ["SU"] = DayOfWeek.Sunday
    };

    private static readonly HashSet<string> RecognizedUnsupportedKeys = new(StringComparer.Ordinal)
    {
        "COUNT", "UNTIL", "BYHOUR", "BYMINUTE", "BYSECOND", "BYMONTH", "BYMONTHDAY",
        "BYSETPOS", "BYYEARDAY", "BYWEEKNO", "WKST", "RSCALE", "SKIP"
    };

    /// <summary>Parses <paramref name="value"/> or throws <see cref="ArgumentException"/>.</summary>
    public static RecurrenceRule Parse(string? value)
    {
        if (!TryParse(value, out var rule, out var error))
            throw new ArgumentException(error);
        return rule;
    }

    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out RecurrenceRule? rule,
        [NotNullWhen(false)] out string? error)
    {
        rule = null;

        if (string.IsNullOrEmpty(value))
        {
            error = "RecurrenceRule is required.";
            return false;
        }

        var components = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in value.Split(';'))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 || separator == part.Length - 1)
            {
                error = $"RecurrenceRule component '{part}' must have the form KEY=VALUE.";
                return false;
            }

            var key = part[..separator];
            var componentValue = part[(separator + 1)..];

            if (RecognizedUnsupportedKeys.Contains(key))
            {
                error = $"RecurrenceRule component '{key}' is not supported. Supported components: FREQ, INTERVAL, BYDAY.";
                return false;
            }

            if (key is not ("FREQ" or "INTERVAL" or "BYDAY"))
            {
                error = $"RecurrenceRule component '{key}' is unknown. Supported components: FREQ, INTERVAL, BYDAY.";
                return false;
            }

            if (!components.TryAdd(key, componentValue))
            {
                error = $"RecurrenceRule component '{key}' appears more than once.";
                return false;
            }
        }

        if (!components.TryGetValue("FREQ", out var freqValue))
        {
            error = "RecurrenceRule must include FREQ.";
            return false;
        }

        RecurrenceFrequency frequency;
        switch (freqValue)
        {
            case "DAILY":
                frequency = RecurrenceFrequency.Daily;
                break;
            case "WEEKLY":
                frequency = RecurrenceFrequency.Weekly;
                break;
            default:
                error = $"RecurrenceRule FREQ '{freqValue}' is not supported. Supported values: DAILY, WEEKLY.";
                return false;
        }

        var interval = 1;
        if (components.TryGetValue("INTERVAL", out var intervalValue))
        {
            // Digits only: no sign, whitespace or leading '+'.
            if (intervalValue.Length > 2 ||
                !intervalValue.All(char.IsAsciiDigit) ||
                !int.TryParse(intervalValue, out interval) ||
                interval < MinInterval || interval > MaxInterval)
            {
                error = $"RecurrenceRule INTERVAL must be an integer from {MinInterval} to {MaxInterval}.";
                return false;
            }
        }

        var byDay = new List<DayOfWeek>();
        if (components.TryGetValue("BYDAY", out var byDayValue))
        {
            if (frequency != RecurrenceFrequency.Weekly)
            {
                error = "RecurrenceRule BYDAY is only supported with FREQ=WEEKLY.";
                return false;
            }

            foreach (var code in byDayValue.Split(','))
            {
                if (!WeekdayCodes.TryGetValue(code, out var day))
                {
                    error = $"RecurrenceRule BYDAY value '{code}' is not a supported weekday code (MO, TU, WE, TH, FR, SA, SU).";
                    return false;
                }

                if (byDay.Contains(day))
                {
                    error = $"RecurrenceRule BYDAY value '{code}' appears more than once.";
                    return false;
                }

                byDay.Add(day);
            }

            byDay.Sort((a, b) => MondayFirst(a).CompareTo(MondayFirst(b)));
        }

        rule = new RecurrenceRule(frequency, interval, byDay);
        error = null;
        return true;
    }

    /// <summary>0 for Monday .. 6 for Sunday (RFC 5545 default WKST=MO).</summary>
    internal static int MondayFirst(DayOfWeek day) => ((int)day + 6) % 7;
}
