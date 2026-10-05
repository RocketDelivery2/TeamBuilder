using FluentAssertions;
using TeamBuilder.Application.Scheduling;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Time zone validation, DST resolution and occurrence generation, using real IANA zones so
/// results never depend on the machine's local time zone.
/// </summary>
public class SeriesSchedulingTests
{
    private static TimeZoneInfo Zone(string id)
    {
        IanaTimeZone.TryResolve(id, out var zone, out var error).Should().BeTrue(error);
        return zone!;
    }

    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("America/New_York")]
    [InlineData("America/Detroit")]
    [InlineData("Europe/London")]
    [InlineData("Asia/Kolkata")]
    [InlineData("UTC")]
    public void ValidIanaTimeZones_AreAccepted(string id)
    {
        IanaTimeZone.TryResolve(id, out var zone, out var error).Should().BeTrue(error);
        zone!.Id.Should().Be(id);
    }

    [Theory]
    [InlineData("Eastern Standard Time")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("america/new_york")]
    [InlineData(" America/New_York")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("+05:00")]
    public void InvalidOrNonIanaTimeZones_AreRejected(string? id)
    {
        IanaTimeZone.TryResolve(id, out var zone, out var error).Should().BeFalse();
        zone.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void TuesdayFivePm_StaysFivePmLocal_AcrossFallBack()
    {
        var newYork = Zone("America/New_York");
        var rule = RecurrenceRuleParser.Parse("FREQ=WEEKLY;BYDAY=TU");

        // 2026-11-01 is the US fall-back Sunday.
        var starts = RecurrenceSchedule.Enumerate(rule, new DateOnly(2026, 10, 27), null, new DateOnly(2026, 11, 10))
            .Select(d => SeriesLocalTime.ToUtc(d.Date, new TimeOnly(17, 0), newYork))
            .ToList();

        starts.Should().Equal(Utc(2026, 10, 27, 21), Utc(2026, 11, 3, 22), Utc(2026, 11, 10, 22));
        starts.Should().OnlyContain(s => s.Kind == DateTimeKind.Utc);
        starts.Select(s => TimeZoneInfo.ConvertTimeFromUtc(s, newYork).TimeOfDay)
            .Should().OnlyContain(t => t == new TimeSpan(17, 0, 0));
    }

    [Fact]
    public void TuesdayFivePm_StaysFivePmLocal_AcrossSpringForward()
    {
        var detroit = Zone("America/Detroit");

        // 2027-03-14 is the US spring-forward Sunday.
        SeriesLocalTime.ToUtc(new DateOnly(2027, 3, 9), new TimeOnly(17, 0), detroit).Should().Be(Utc(2027, 3, 9, 22));
        SeriesLocalTime.ToUtc(new DateOnly(2027, 3, 16), new TimeOnly(17, 0), detroit).Should().Be(Utc(2027, 3, 16, 21));
    }

    [Theory]
    [InlineData("America/New_York")]
    [InlineData("America/Detroit")]
    public void SpringForwardGap_AdvancesToFirstValidLocalInstantAfterTheGap(string zoneId)
    {
        var zone = Zone(zoneId);

        // 02:30 does not exist on 2027-03-14; the gap ends at 03:00 EDT (07:00Z).
        var start = SeriesLocalTime.ToUtc(new DateOnly(2027, 3, 14), new TimeOnly(2, 30), zone);

        start.Should().Be(Utc(2027, 3, 14, 7));
        TimeZoneInfo.ConvertTimeFromUtc(start, zone).Should().Be(new DateTime(2027, 3, 14, 3, 0, 0));

        // The days around it keep 02:30 local.
        SeriesLocalTime.ToUtc(new DateOnly(2027, 3, 13), new TimeOnly(2, 30), zone).Should().Be(Utc(2027, 3, 13, 7, 30));
        SeriesLocalTime.ToUtc(new DateOnly(2027, 3, 15), new TimeOnly(2, 30), zone).Should().Be(Utc(2027, 3, 15, 6, 30));
    }

    [Fact]
    public void SpringForwardGap_AtTheVeryStartOfTheGap_AlsoAdvances()
    {
        SeriesLocalTime.ToUtc(new DateOnly(2027, 3, 14), new TimeOnly(2, 0), Zone("America/New_York"))
            .Should().Be(Utc(2027, 3, 14, 7));
    }

    [Theory]
    [InlineData("America/New_York")]
    [InlineData("America/Detroit")]
    public void FallBackOverlap_ChoosesStandardTime(string zoneId)
    {
        var zone = Zone(zoneId);

        // 01:30 happens twice on 2026-11-01: 05:30Z (EDT) and 06:30Z (EST). Standard wins.
        var start = SeriesLocalTime.ToUtc(new DateOnly(2026, 11, 1), new TimeOnly(1, 30), zone);

        start.Should().Be(Utc(2026, 11, 1, 6, 30));
        zone.IsDaylightSavingTime(new DateTimeOffset(start)).Should().BeFalse();
    }

    [Fact]
    public void UtcZone_GeneratesTheLocalTimeAsUtc()
    {
        var series = NewSeries("UTC", "FREQ=DAILY", new DateOnly(2027, 3, 13), new TimeOnly(17, 0));

        var occurrences = Generate(series, new DateOnly(2027, 3, 15));

        occurrences.Select(o => o.ScheduledStartUtc).Should().Equal(
            Utc(2027, 3, 13, 17), Utc(2027, 3, 14, 17), Utc(2027, 3, 15, 17));
    }

    [Fact]
    public void Generator_CopiesSeriesTemplate_AndSetsPlannedAttachedOccurrences()
    {
        var series = NewSeries("America/New_York", "FREQ=WEEKLY;BYDAY=TU", new DateOnly(2026, 10, 13), new TimeOnly(17, 0));
        series.TeamId = Guid.NewGuid();
        series.HostId = Guid.NewGuid();
        series.VenueId = Guid.NewGuid();
        series.Description = "Bring water";
        series.Category = "Basketball";
        series.Tags = "pickup,indoor";
        series.MaxParticipants = 12;
        series.DurationMinutes = 90;

        var occurrences = Generate(series, series.SeriesStartDate.AddDays(20));

        occurrences.Should().HaveCount(3);
        occurrences.Select(o => o.OccurrenceIndex).Should().Equal(0, 1, 2);
        foreach (var o in occurrences)
        {
            o.SeriesId.Should().Be(series.Id);
            o.TeamId.Should().Be(series.TeamId);
            o.HostId.Should().Be(series.HostId);
            o.VenueId.Should().Be(series.VenueId);
            o.Name.Should().Be(series.Name);
            o.Description.Should().Be("Bring water");
            o.Category.Should().Be("Basketball");
            o.Tags.Should().Be("pickup,indoor");
            o.MaxParticipants.Should().Be(12);
            o.CurrentParticipantCount.Should().Be(0);
            o.Status.Should().Be(EventStatus.Planned);
            o.IsDetached.Should().BeFalse();
            o.LegacyLocation.Should().BeNull();
            o.ScheduledEndUtc.Should().Be(o.ScheduledStartUtc.AddMinutes(90));
            o.Id.Should().NotBeEmpty();
        }
    }

    [Fact]
    public void Generator_SkipsExistingStartsAndIndices()
    {
        var series = NewSeries("UTC", "FREQ=DAILY", new DateOnly(2027, 1, 1), new TimeOnly(9, 0));

        var occurrences = EventSeriesOccurrenceGenerator.Generate(
            series,
            RecurrenceRuleParser.Parse(series.RecurrenceRule),
            Zone("UTC"),
            new DateOnly(2027, 1, 4),
            existingStartsUtc: new HashSet<DateTime> { Utc(2027, 1, 1, 9) },
            existingIndices: new HashSet<int> { 2 });

        occurrences.Select(o => o.OccurrenceIndex).Should().Equal(1, 3);
    }

    internal static EventSeries NewSeries(string timeZoneId, string rrule, DateOnly start, TimeOnly time) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Series",
        TimeZoneId = timeZoneId,
        RecurrenceRule = rrule,
        SeriesStartDate = start,
        LocalStartTime = time,
        DurationMinutes = 60,
        MaxParticipants = 10,
        Status = EventSeriesStatus.Active
    };

    private static List<EventOccurrence> Generate(EventSeries series, DateOnly through) =>
        EventSeriesOccurrenceGenerator.Generate(
            series, RecurrenceRuleParser.Parse(series.RecurrenceRule), Zone(series.TimeZoneId), through);
}
