using FluentAssertions;
using TeamBuilder.Application.Scheduling;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Range enumeration must assign exactly the occurrence indices full enumeration from the series
/// start assigns, for every window, while doing work proportional to the window only.
/// </summary>
public class RecurrenceRangeTests
{
    public static TheoryData<string, string> Rules => new()
    {
        { "FREQ=DAILY", "2026-10-13" },
        { "FREQ=DAILY;INTERVAL=3", "2026-10-13" },
        { "FREQ=DAILY;INTERVAL=52", "2026-10-16" },
        { "FREQ=WEEKLY", "2026-10-13" },
        { "FREQ=WEEKLY;BYDAY=TU", "2026-10-13" },
        { "FREQ=WEEKLY;INTERVAL=2;BYDAY=TU", "2026-10-13" },
        { "FREQ=WEEKLY;INTERVAL=3", "2026-10-18" },
        // Start mid-week: week 0 only yields the BYDAY days on or after the start.
        { "FREQ=WEEKLY;BYDAY=MO,WE,FR", "2026-10-14" },
        { "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,TH,SU", "2026-10-16" },
        { "FREQ=WEEKLY;BYDAY=SA,SU", "2026-10-11" },
        { "FREQ=WEEKLY;INTERVAL=5;BYDAY=MO,TU,WE,TH,FR,SA,SU", "2026-10-15" },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void EveryWindow_MatchesFullEnumeration(string rrule, string start)
    {
        var rule = RecurrenceRuleParser.Parse(rrule);
        var seriesStart = DateOnly.Parse(start);
        var horizon = seriesStart.AddDays(400);
        var full = RecurrenceSchedule.Enumerate(rule, seriesStart, null, horizon).ToList();

        // Windows starting before the series, on it, and on every day of the next ~year.
        for (var from = seriesStart.AddDays(-10); from <= seriesStart.AddDays(370); from = from.AddDays(1))
        {
            foreach (var length in new[] { 0, 1, 6, 20, 30 })
            {
                var through = from.AddDays(length);
                var expected = full.Where(d => d.Date >= from && d.Date <= through).ToList();

                RecurrenceSchedule.Enumerate(rule, seriesStart, null, from, through)
                    .Should().Equal(expected, $"{rrule} window {from}..{through}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Rules))]
    public void RangeRespectsSeriesEndDate(string rrule, string start)
    {
        var rule = RecurrenceRuleParser.Parse(rrule);
        var seriesStart = DateOnly.Parse(start);
        var end = seriesStart.AddDays(45);
        var full = RecurrenceSchedule.Enumerate(rule, seriesStart, end, end.AddDays(100)).ToList();

        RecurrenceSchedule.Enumerate(rule, seriesStart, end, seriesStart.AddDays(30), seriesStart.AddDays(90))
            .Should().Equal(full.Where(d => d.Date >= seriesStart.AddDays(30)));
        RecurrenceSchedule.Enumerate(rule, seriesStart, end, end.AddDays(1), end.AddDays(30)).Should().BeEmpty();
    }

    [Fact]
    public void DailyIndices_AreStableAcrossRollingWindows()
    {
        var rule = RecurrenceRuleParser.Parse("FREQ=DAILY");
        var start = new DateOnly(2026, 10, 13);

        var stitched = new List<RecurrenceDate>();
        var last = start.AddDays(119);
        for (var from = start; from <= last; from = from.AddDays(7))
        {
            var through = from.AddDays(6) < last ? from.AddDays(6) : last;
            stitched.AddRange(RecurrenceSchedule.Enumerate(rule, start, null, from, through));
        }

        stitched.Should().Equal(RecurrenceSchedule.Enumerate(rule, start, null, start.AddDays(119)));
        stitched.Select(d => d.Index).Should().Equal(Enumerable.Range(0, 120));
    }

    [Fact]
    public void WeeklyIntervalTwoMultiDay_KnownIndices()
    {
        // Start Wed 2026-10-14; week 0 (Mon 10-12) yields WE, FR; week 1 skipped; week 2 (10-26) yields MO, WE, FR.
        var rule = RecurrenceRuleParser.Parse("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE,FR");
        var start = new DateOnly(2026, 10, 14);

        RecurrenceSchedule.Enumerate(rule, start, null, new DateOnly(2026, 10, 20), new DateOnly(2026, 11, 13))
            .Should().Equal(
                new RecurrenceDate(2, new DateOnly(2026, 10, 26)),
                new RecurrenceDate(3, new DateOnly(2026, 10, 28)),
                new RecurrenceDate(4, new DateOnly(2026, 10, 30)),
                new RecurrenceDate(5, new DateOnly(2026, 11, 9)),
                new RecurrenceDate(6, new DateOnly(2026, 11, 11)),
                new RecurrenceDate(7, new DateOnly(2026, 11, 13)));
    }

    [Fact]
    public void YearsOldSeries_RangeIsComputedArithmetically()
    {
        // A daily series started 2000-01-01: the window in 2026 has index = days since start,
        // without enumerating 26 years first.
        var rule = RecurrenceRuleParser.Parse("FREQ=DAILY");
        var start = new DateOnly(2000, 1, 1);
        var from = new DateOnly(2026, 10, 13);

        var window = RecurrenceSchedule.Enumerate(rule, start, null, from, from.AddDays(20)).ToList();

        window.Should().HaveCount(21);
        window[0].Should().Be(new RecurrenceDate(from.DayNumber - start.DayNumber, from));

        var weekly = RecurrenceRuleParser.Parse("FREQ=WEEKLY;BYDAY=TU,TH");
        var weeklyStart = new DateOnly(2000, 1, 4); // Tuesday
        var weeklyWindow = RecurrenceSchedule.Enumerate(weekly, weeklyStart, null, from, from.AddDays(6)).ToList();
        weeklyWindow.Select(d => d.Index).Should().Equal(
            RecurrenceSchedule.Enumerate(weekly, weeklyStart, null, from.AddDays(6)).Where(d => d.Date >= from).Select(d => d.Index));
    }

    [Fact]
    public void RangeNearDateOnlyMaxValue_DoesNotOverflow()
    {
        var rule = RecurrenceRuleParser.Parse("FREQ=WEEKLY;INTERVAL=52;BYDAY=SU");
        var start = new DateOnly(9999, 1, 4);

        var act = () => RecurrenceSchedule.Enumerate(rule, start, null, new DateOnly(9999, 6, 1), DateOnly.MaxValue).ToList();

        act.Should().NotThrow();
        RecurrenceSchedule.Enumerate(RecurrenceRuleParser.Parse("FREQ=DAILY;INTERVAL=7"), start, null, new DateOnly(9999, 12, 1), DateOnly.MaxValue)
            .Should().OnlyContain(d => d.Date <= DateOnly.MaxValue);
    }

    [Fact]
    public void Generator_RangeOverload_KeepsFullGenerationIndicesAndInstants()
    {
        var series = SeriesSchedulingTests.NewSeries("America/New_York", "FREQ=WEEKLY;INTERVAL=2;BYDAY=TU,SA", new DateOnly(2026, 9, 1), new TimeOnly(2, 30));
        var rule = RecurrenceRuleParser.Parse(series.RecurrenceRule);
        IanaTimeZone.TryResolve(series.TimeZoneId, out var zone, out _).Should().BeTrue();

        var full = EventSeriesOccurrenceGenerator.Generate(series, rule, zone!, new DateOnly(2027, 4, 30));
        var range = EventSeriesOccurrenceGenerator.Generate(series, rule, zone!, new DateOnly(2027, 3, 1), new DateOnly(2027, 4, 30));

        range.Select(o => (o.OccurrenceIndex, o.ScheduledStartUtc)).Should().Equal(
            full.Where(o => o.ScheduledStartUtc >= new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc))
                .Select(o => (o.OccurrenceIndex, o.ScheduledStartUtc)));
    }
}
