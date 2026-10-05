using FluentAssertions;
using TeamBuilder.Application.Scheduling;

namespace TeamBuilder.Tests.Application;

public class RecurrenceRuleParserTests
{
    // 2026-10-13 is a Tuesday.
    private static readonly DateOnly Tuesday = new(2026, 10, 13);

    private static List<DateOnly> Dates(string rrule, DateOnly start, DateOnly through, DateOnly? end = null) =>
        RecurrenceSchedule.Enumerate(RecurrenceRuleParser.Parse(rrule), start, end, through).Select(d => d.Date).ToList();

    [Fact]
    public void Daily_DefaultsIntervalToOne()
    {
        var rule = RecurrenceRuleParser.Parse("FREQ=DAILY");

        rule.Frequency.Should().Be(RecurrenceFrequency.Daily);
        rule.Interval.Should().Be(1);
        rule.ByDay.Should().BeEmpty();
        Dates("FREQ=DAILY", Tuesday, Tuesday.AddDays(3)).Should().Equal(
            Tuesday, Tuesday.AddDays(1), Tuesday.AddDays(2), Tuesday.AddDays(3));
    }

    [Fact]
    public void Daily_IntervalTwo_SkipsEveryOtherDay()
    {
        RecurrenceRuleParser.Parse("FREQ=DAILY;INTERVAL=2").Interval.Should().Be(2);
        Dates("FREQ=DAILY;INTERVAL=2", Tuesday, Tuesday.AddDays(6)).Should().Equal(
            Tuesday, Tuesday.AddDays(2), Tuesday.AddDays(4), Tuesday.AddDays(6));
    }

    [Fact]
    public void Weekly_WithoutByDay_UsesTheStartDatesWeekday()
    {
        var friday = new DateOnly(2026, 10, 16);

        Dates("FREQ=WEEKLY", friday, friday.AddDays(21)).Should().Equal(
            friday, friday.AddDays(7), friday.AddDays(14), friday.AddDays(21));
    }

    [Fact]
    public void Weekly_ByDayTuesday()
    {
        var rule = RecurrenceRuleParser.Parse("FREQ=WEEKLY;BYDAY=TU");
        rule.ByDay.Should().Equal(DayOfWeek.Tuesday);

        // Starting on a Monday: the first occurrence is the next day.
        Dates("FREQ=WEEKLY;BYDAY=TU", Tuesday.AddDays(-1), Tuesday.AddDays(14)).Should().Equal(
            Tuesday, Tuesday.AddDays(7), Tuesday.AddDays(14));
    }

    [Fact]
    public void Weekly_MultipleByDay_InWeekOrder_NeverBeforeStart()
    {
        RecurrenceRuleParser.Parse("FREQ=WEEKLY;BYDAY=TH,MO,TU").ByDay
            .Should().Equal(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Thursday);

        // Start Tuesday: that week's Monday is before the start and is skipped.
        Dates("FREQ=WEEKLY;BYDAY=TH,MO,TU", Tuesday, Tuesday.AddDays(9)).Should().Equal(
            Tuesday, Tuesday.AddDays(2), Tuesday.AddDays(6), Tuesday.AddDays(7), Tuesday.AddDays(9));
    }

    [Fact]
    public void Weekly_IntervalTwo_UsesEveryOtherMondayStartingWeek()
    {
        // Start Thursday 2026-10-15; week 0 is Mon 10-12..Sun 10-18, week 2 is 10-26.., week 4 is 11-09..
        var thursday = new DateOnly(2026, 10, 15);

        Dates("FREQ=WEEKLY;INTERVAL=2;BYDAY=TU,TH", thursday, new DateOnly(2026, 11, 15)).Should().Equal(
            new DateOnly(2026, 10, 15),
            new DateOnly(2026, 10, 27), new DateOnly(2026, 10, 29),
            new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 12));
    }

    [Fact]
    public void Indices_AreZeroBasedFromSeriesStart_AndStableAcrossWindows()
    {
        var rule = RecurrenceRuleParser.Parse("FREQ=WEEKLY;BYDAY=TU,TH");
        var shortWindow = RecurrenceSchedule.Enumerate(rule, Tuesday, null, Tuesday.AddDays(10)).ToList();
        var longWindow = RecurrenceSchedule.Enumerate(rule, Tuesday, null, Tuesday.AddDays(40)).ToList();

        shortWindow.Select(d => d.Index).Should().Equal(0, 1, 2, 3);
        longWindow.Take(shortWindow.Count).Should().Equal(shortWindow);
        longWindow.Select(d => d.Index).Should().Equal(Enumerable.Range(0, longWindow.Count));
    }

    [Fact]
    public void SeriesEndDate_BoundsTheRecurrence()
    {
        Dates("FREQ=DAILY", Tuesday, Tuesday.AddDays(30), end: Tuesday.AddDays(2)).Should().HaveCount(3);
        Dates("FREQ=DAILY", Tuesday, Tuesday.AddDays(-1)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("FREQ=MONTHLY")]
    [InlineData("FREQ=YEARLY")]
    [InlineData("FREQ=HOURLY")]
    [InlineData("FREQ=daily")]
    [InlineData("FREQ=")]
    [InlineData("INTERVAL=2")]
    public void InvalidOrMissingFreq_IsRejected(string value)
    {
        RecurrenceRuleParser.TryParse(value, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
        FluentActions.Invoking(() => RecurrenceRuleParser.Parse(value)).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("FREQ=DAILY;FREQ=DAILY")]
    [InlineData("FREQ=WEEKLY;BYDAY=TU;BYDAY=TH")]
    [InlineData("FREQ=DAILY;INTERVAL=2;INTERVAL=2")]
    public void DuplicateKeys_AreRejected(string value)
    {
        RecurrenceRuleParser.TryParse(value, out _, out var error).Should().BeFalse();
        error.Should().Contain("more than once");
    }

    [Theory]
    [InlineData("FREQ=DAILY;FOO=BAR")]
    [InlineData("FREQ=DAILY;X-NAME=1")]
    [InlineData("FREQ=DAILY;")]
    [InlineData("RRULE:FREQ=DAILY")]
    [InlineData("FREQ=DAILY;freq=DAILY")]
    [InlineData("FREQ=WEEKLY;WKST=SU")]
    public void UnknownOrMalformedComponents_AreRejected(string value)
    {
        RecurrenceRuleParser.TryParse(value, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("FREQ=WEEKLY;BYDAY=XX")]
    [InlineData("FREQ=WEEKLY;BYDAY=tu")]
    [InlineData("FREQ=WEEKLY;BYDAY=1TU")]
    [InlineData("FREQ=WEEKLY;BYDAY=-1FR")]
    [InlineData("FREQ=WEEKLY;BYDAY=TU,")]
    [InlineData("FREQ=WEEKLY;BYDAY=TU,TU")]
    [InlineData("FREQ=DAILY;BYDAY=TU")]
    public void InvalidByDay_IsRejected(string value)
    {
        RecurrenceRuleParser.TryParse(value, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+2")]
    [InlineData("53")]
    [InlineData("1000")]
    [InlineData("two")]
    [InlineData(" 2")]
    public void InvalidInterval_IsRejected(string interval)
    {
        RecurrenceRuleParser.TryParse($"FREQ=DAILY;INTERVAL={interval}", out _, out var error).Should().BeFalse();
        error.Should().Contain("INTERVAL");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("52")]
    public void IntervalBounds_AreAccepted(string interval)
    {
        RecurrenceRuleParser.TryParse($"FREQ=WEEKLY;INTERVAL={interval}", out var rule, out _).Should().BeTrue();
        rule!.Interval.Should().Be(int.Parse(interval));
    }

    [Theory]
    [InlineData("COUNT=10")]
    [InlineData("UNTIL=20261231T000000Z")]
    [InlineData("BYHOUR=17")]
    [InlineData("BYMINUTE=30")]
    [InlineData("BYSECOND=0")]
    [InlineData("BYMONTH=10")]
    [InlineData("BYMONTHDAY=13")]
    [InlineData("BYSETPOS=1")]
    public void UnsupportedRfc5545Components_AreRejected(string component)
    {
        RecurrenceRuleParser.TryParse($"FREQ=WEEKLY;{component}", out _, out var error).Should().BeFalse();
        error.Should().Contain("not supported");
    }
}
