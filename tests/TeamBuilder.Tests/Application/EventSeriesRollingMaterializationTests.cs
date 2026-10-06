using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Scheduling;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// The rolling horizon (MaintainHorizonAsync) and checkpoint rules, on the in-memory provider
/// with a controllable clock. Concurrency and transactional behavior are proven on SQL Server in
/// <see cref="EventSeriesRollingSqlServerIntegrationTests"/>.
/// </summary>
public class EventSeriesRollingMaterializationTests
{
    // Monday 2026-10-05 12:00 UTC (08:00 in New York).
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 5);

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly FixedTimeProvider _clock = new(Now);

    [Fact]
    public async Task NewSeriesStartingToday_IsAlreadyUpToDate()
    {
        var series = await CreateAsync("FREQ=DAILY", Today);

        var result = await Materializer().MaintainHorizonAsync(series.Id);

        result.Outcome.Should().Be(EventSeriesMaterializationOutcome.UpToDate);
        result.CheckpointAfter.Should().Be(Today.AddDays(20));
        (await CountAsync(series.Id)).Should().Be(21);
    }

    [Fact]
    public async Task DailySeries_KeepsTwentyOneLocalDaysAvailable_AsTimePasses()
    {
        var series = await CreateAsync("FREQ=DAILY", Today);

        _clock.UtcNow = Now.AddDays(3);
        var result = await Materializer().MaintainHorizonAsync(series.Id);

        result.Outcome.Should().Be(EventSeriesMaterializationOutcome.Materialized);
        result.Inserted.Should().Be(3);
        result.CheckpointBefore.Should().Be(Today.AddDays(20));
        result.CheckpointAfter.Should().Be(Today.AddDays(23));
        var occurrences = await OccurrencesAsync(series.Id);
        occurrences.Select(o => o.OccurrenceIndex).Should().Equal(Enumerable.Range(0, 24).Cast<int?>());
        occurrences.Count(o => o.ScheduledStartUtc >= _clock.UtcNow.UtcDateTime.Date).Should().Be(21);
        (await CheckpointAsync(series.Id)).Should().Be(Today.AddDays(23));
    }

    [Fact]
    public async Task WeeklySeries_AddsTheNewlyNeededOccurrence()
    {
        // Tuesdays from 10-06: initial window 10-06..10-26 holds 10-06, 10-13, 10-20.
        var series = await CreateAsync("FREQ=WEEKLY;BYDAY=TU", new DateOnly(2026, 10, 6));
        (await CountAsync(series.Id)).Should().Be(3);

        _clock.UtcNow = Now.AddDays(1); // 10-06: target 10-26, nothing new.
        (await Materializer().MaintainHorizonAsync(series.Id)).Outcome.Should().Be(EventSeriesMaterializationOutcome.UpToDate);

        _clock.UtcNow = Now.AddDays(2); // 10-07: target 10-27, a Tuesday.
        var result = await Materializer().MaintainHorizonAsync(series.Id);

        result.Inserted.Should().Be(1);
        var added = (await OccurrencesAsync(series.Id)).Last();
        added.OccurrenceIndex.Should().Be(3);
        added.ScheduledStartUtc.Should().Be(new DateTime(2026, 10, 27, 21, 0, 0));
    }

    [Fact]
    public async Task WeeklySeries_CheckpointAdvancesOnDaysWithoutOccurrences()
    {
        var series = await CreateAsync("FREQ=WEEKLY;BYDAY=TU", new DateOnly(2026, 10, 6));

        _clock.UtcNow = Now.AddDays(4); // 10-09: target 10-29 adds Tuesday 10-27.
        var result = await Materializer().MaintainHorizonAsync(series.Id);

        result.Inserted.Should().Be(1);
        result.CheckpointAfter.Should().Be(new DateOnly(2026, 10, 29));

        _clock.UtcNow = Now.AddDays(5); // 10-10: target 10-30, a Friday: checkpoint only.
        var next = await Materializer().MaintainHorizonAsync(series.Id);
        next.Outcome.Should().Be(EventSeriesMaterializationOutcome.Materialized);
        next.Inserted.Should().Be(0);
        next.CheckpointAfter.Should().Be(new DateOnly(2026, 10, 30));
    }

    [Fact]
    public async Task AlreadyThroughTarget_DoesNothing_AndRepeatedPassesAreIdempotent()
    {
        var series = await CreateAsync("FREQ=DAILY", Today);
        _clock.UtcNow = Now.AddDays(2);

        (await Materializer().MaintainHorizonAsync(series.Id)).Inserted.Should().Be(2);
        for (var i = 0; i < 3; i++)
        {
            var again = await Materializer().MaintainHorizonAsync(series.Id);
            again.Outcome.Should().Be(EventSeriesMaterializationOutcome.UpToDate);
            again.Inserted.Should().Be(0);
        }

        var starts = (await OccurrencesAsync(series.Id)).Select(o => o.ScheduledStartUtc).ToList();
        starts.Should().HaveCount(23).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task EndedSeries_DoesNothing()
    {
        var series = await CreateAsync("FREQ=DAILY", Today, endDate: Today.AddDays(4));
        _clock.UtcNow = Now.AddDays(5);

        var result = await Materializer().MaintainHorizonAsync(series.Id);

        result.Outcome.Should().Be(EventSeriesMaterializationOutcome.Ended);
        (await CountAsync(series.Id)).Should().Be(5);
        (await CheckpointAsync(series.Id)).Should().Be(Today.AddDays(4));
    }

    [Fact]
    public async Task BoundedSeries_RollingCheckpointStopsAtSeriesEndDate()
    {
        var series = await CreateAsync("FREQ=DAILY", Today, endDate: Today.AddDays(25));
        _clock.UtcNow = Now.AddDays(10);

        var result = await Materializer().MaintainHorizonAsync(series.Id);

        result.Inserted.Should().Be(5);
        result.CheckpointAfter.Should().Be(Today.AddDays(25));
        _clock.UtcNow = Now.AddDays(11);
        (await Materializer().MaintainHorizonAsync(series.Id)).Outcome.Should().Be(EventSeriesMaterializationOutcome.UpToDate);
    }

    [Theory]
    [InlineData(EventSeriesStatus.Cancelled)]
    [InlineData(EventSeriesStatus.Paused)]
    [InlineData(EventSeriesStatus.Completed)]
    public async Task NonActiveSeries_DoesNothing(EventSeriesStatus status)
    {
        var series = await CreateAsync("FREQ=DAILY", Today);
        await using (var context = Context())
        {
            var stored = await context.EventSeries.SingleAsync(s => s.Id == series.Id);
            stored.Status = status;
            await context.SaveChangesAsync();
        }

        _clock.UtcNow = Now.AddDays(7);
        var result = await Materializer().MaintainHorizonAsync(series.Id);

        result.Outcome.Should().Be(EventSeriesMaterializationOutcome.NotActive);
        (await CountAsync(series.Id)).Should().Be(21);
        (await CheckpointAsync(series.Id)).Should().Be(Today.AddDays(20));
    }

    [Fact]
    public async Task MissingSeries_ReportsNotFound()
    {
        (await Materializer().MaintainHorizonAsync(Guid.NewGuid())).Outcome.Should().Be(EventSeriesMaterializationOutcome.NotFound);
    }

    [Fact]
    public async Task FutureStartingSeries_WaitsUntilItsHorizonMoves()
    {
        var start = new DateOnly(2026, 11, 20);
        var series = await CreateAsync("FREQ=DAILY", start);
        (await CheckpointAsync(series.Id)).Should().Be(start.AddDays(20));

        // Today's target (10-25) is before the start: nothing to do.
        (await Materializer().MaintainHorizonAsync(series.Id)).Outcome.Should().Be(EventSeriesMaterializationOutcome.UpToDate);

        // 11-25: target 12-15, five days past the initial 12-10 checkpoint.
        _clock.UtcNow = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        var result = await Materializer().MaintainHorizonAsync(series.Id);

        result.Inserted.Should().Be(5);
        (await OccurrencesAsync(series.Id)).Select(o => o.OccurrenceIndex).Should().Equal(Enumerable.Range(0, 26).Cast<int?>());
    }

    [Fact]
    public async Task FutureStartingSeriesWithUnknownCheckpoint_RecordsTheEvaluatedHorizonOnly()
    {
        var seriesId = await AddBareSeriesAsync("America/New_York", "FREQ=DAILY", new DateOnly(2026, 12, 1));

        var result = await Materializer().MaintainHorizonAsync(seriesId);

        result.Inserted.Should().Be(0);
        result.CheckpointAfter.Should().Be(Today.AddDays(20));
        (await CountAsync(seriesId)).Should().Be(0);
    }

    [Fact]
    public async Task Target_UsesTheSeriesTimeZone_NotTheServerOrUtcDate()
    {
        // 2026-10-06 02:00Z is still 10-05 in New York but already 10-06 in Tokyo and London.
        _clock.UtcNow = new DateTimeOffset(2026, 10, 6, 2, 0, 0, TimeSpan.Zero);
        var newYork = await AddBareSeriesAsync("America/New_York", "FREQ=DAILY", Today);
        var tokyo = await AddBareSeriesAsync("Asia/Tokyo", "FREQ=DAILY", Today);
        var honolulu = await AddBareSeriesAsync("Pacific/Honolulu", "FREQ=DAILY", Today);

        (await Materializer().MaintainHorizonAsync(newYork)).CheckpointAfter.Should().Be(new DateOnly(2026, 10, 25));
        (await Materializer().MaintainHorizonAsync(tokyo)).CheckpointAfter.Should().Be(new DateOnly(2026, 10, 26));
        (await Materializer().MaintainHorizonAsync(honolulu)).CheckpointAfter.Should().Be(new DateOnly(2026, 10, 25));

        // Unknown checkpoint resumes at local today: New York 10-05 (21 rows), Tokyo 10-06 (21 rows).
        (await OccurrencesAsync(newYork)).Select(o => o.OccurrenceIndex).Should().Equal(Enumerable.Range(0, 21).Cast<int?>());
        (await OccurrencesAsync(tokyo)).Select(o => o.OccurrenceIndex).Should().Equal(Enumerable.Range(1, 21).Cast<int?>());
    }

    [Fact]
    public async Task UnknownCheckpoint_RecoversAnExistingSeries_WithoutDuplicates()
    {
        // A series as #170 created it (21 rows from its start), before checkpoints existed.
        var series = await CreateAsync("FREQ=DAILY", Today);
        var before = await OccurrencesAsync(series.Id);
        await SetCheckpointAsync(series.Id, null);

        _clock.UtcNow = Now.AddDays(5);
        var result = await Materializer().MaintainHorizonAsync(series.Id);

        result.Inserted.Should().Be(5);
        result.CheckpointBefore.Should().BeNull();
        result.CheckpointAfter.Should().Be(Today.AddDays(25));
        var after = await OccurrencesAsync(series.Id);
        after.Should().HaveCount(26);
        after.Select(o => o.ScheduledStartUtc).Should().OnlyHaveUniqueItems();
        after.Select(o => o.OccurrenceIndex).Should().Equal(Enumerable.Range(0, 26).Cast<int?>());
        after.Take(21).Select(o => o.Id).Should().Equal(before.Select(o => o.Id));
    }

    [Fact]
    public async Task UnknownCheckpoint_DoesNotBackfillPastDates()
    {
        var series = await CreateAsync("FREQ=DAILY", Today);
        await SetCheckpointAsync(series.Id, null);

        _clock.UtcNow = Now.AddDays(40);
        var result = await Materializer().MaintainHorizonAsync(series.Id);

        // Local dates 10-26 .. 11-13 were never materialized and are past: they stay absent.
        result.Inserted.Should().Be(21);
        var indices = (await OccurrencesAsync(series.Id)).Select(o => o.OccurrenceIndex!.Value).ToList();
        indices.Should().Equal(Enumerable.Range(0, 21).Concat(Enumerable.Range(40, 21)));
    }

    [Theory]
    [InlineData("FREQ=DAILY")]
    [InlineData("FREQ=DAILY;INTERVAL=3")]
    [InlineData("FREQ=WEEKLY;BYDAY=TU")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=TU")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,WE,FR")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=TU,TH,SA")]
    public async Task RollingDayByDay_ProducesExactlyTheFullGeneration_AndNeverRenumbers(string rrule)
    {
        var series = await CreateAsync(rrule, new DateOnly(2026, 10, 7)); // a Wednesday
        var ids = new Dictionary<int, Guid>();

        for (var day = 0; day <= 75; day++)
        {
            _clock.UtcNow = Now.AddDays(day);
            await Materializer().MaintainHorizonAsync(series.Id);

            foreach (var occurrence in await OccurrencesAsync(series.Id))
            {
                // Once written, an occurrence keeps its index (and row) forever.
                if (ids.TryGetValue(occurrence.OccurrenceIndex!.Value, out var id))
                    occurrence.Id.Should().Be(id);
                else
                    ids[occurrence.OccurrenceIndex.Value] = occurrence.Id;
            }
        }

        var checkpoint = (await CheckpointAsync(series.Id))!.Value;
        checkpoint.Should().Be(Today.AddDays(75 + 20));
        await using var context = Context();
        var stored = await context.EventSeries.AsNoTracking().SingleAsync(s => s.Id == series.Id);
        IanaTimeZone.TryResolve(stored.TimeZoneId, out var zone, out _).Should().BeTrue();
        var full = EventSeriesOccurrenceGenerator.Generate(stored, RecurrenceRuleParser.Parse(rrule), zone!, checkpoint);

        (await OccurrencesAsync(series.Id)).Select(o => (o.OccurrenceIndex, o.ScheduledStartUtc.Ticks))
            .Should().Equal(full.Select(o => (o.OccurrenceIndex, o.ScheduledStartUtc.Ticks)));
    }

    [Fact]
    public async Task MaterializeRange_BeforeTheResumePoint_AdvancesTheCheckpoint_ButAGappedRangeDoesNot()
    {
        var series = await CreateAsync("FREQ=DAILY", Today);

        // A range that starts after the resume point (10-26) leaves a gap: rows yes, checkpoint no.
        var gapped = await Materializer().MaterializeAsync(series.Id, Today.AddDays(30), Today.AddDays(32));
        gapped.Inserted.Should().Be(3);
        gapped.CheckpointAfter.Should().Be(Today.AddDays(20));

        // A contiguous range advances it (and skips the rows the gapped call already wrote).
        var contiguous = await Materializer().MaterializeAsync(series.Id, Today, Today.AddDays(35));
        contiguous.Inserted.Should().Be(12);
        contiguous.CheckpointAfter.Should().Be(Today.AddDays(35));
        (await OccurrencesAsync(series.Id)).Select(o => o.OccurrenceIndex).Should().Equal(Enumerable.Range(0, 36).Cast<int?>());
    }

    [Fact]
    public async Task MaterializeRange_RejectsAnInvertedRange()
    {
        var series = await CreateAsync("FREQ=DAILY", Today);

        var act = () => Materializer().MaterializeAsync(series.Id, Today.AddDays(2), Today);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private TeamBuilderDbContext Context() =>
        new(new DbContextOptionsBuilder<TeamBuilderDbContext>().UseInMemoryDatabase(_databaseName).Options);

    private EventSeriesMaterializer Materializer() => new(Context(), _clock);

    private Task<EventSeriesDto> CreateAsync(string rrule, DateOnly start, DateOnly? endDate = null) =>
        new EventSeriesService(Context(), _clock).CreateAsync(new CreateEventSeriesDto
        {
            Name = "Pickup",
            RecurrenceRule = rrule,
            TimeZoneId = "America/New_York",
            LocalStartTime = new TimeOnly(17, 0),
            DurationMinutes = 60,
            SeriesStartDate = start,
            SeriesEndDate = endDate,
            MaxParticipants = 10
        }, Guid.NewGuid());

    private async Task<Guid> AddBareSeriesAsync(string timeZoneId, string rrule, DateOnly start)
    {
        await using var context = Context();
        var series = SeriesSchedulingTests.NewSeries(timeZoneId, rrule, start, new TimeOnly(17, 0));
        context.EventSeries.Add(series);
        await context.SaveChangesAsync();
        return series.Id;
    }

    private async Task SetCheckpointAsync(Guid seriesId, DateOnly? checkpoint)
    {
        await using var context = Context();
        var series = await context.EventSeries.SingleAsync(s => s.Id == seriesId);
        series.MaterializedThroughLocalDate = checkpoint;
        await context.SaveChangesAsync();
    }

    private async Task<DateOnly?> CheckpointAsync(Guid seriesId)
    {
        await using var context = Context();
        return (await context.EventSeries.AsNoTracking().SingleAsync(s => s.Id == seriesId)).MaterializedThroughLocalDate;
    }

    private async Task<int> CountAsync(Guid seriesId)
    {
        await using var context = Context();
        return await context.Events.CountAsync(e => e.SeriesId == seriesId);
    }

    private async Task<List<EventOccurrence>> OccurrencesAsync(Guid seriesId)
    {
        await using var context = Context();
        return await context.Events.AsNoTracking()
            .Where(e => e.SeriesId == seriesId)
            .OrderBy(e => e.ScheduledStartUtc)
            .ToListAsync();
    }
}
