using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Persistence;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Series creation, materialization and cancellation on a real, fully migrated SQL Server
/// database: atomic creation, the UX_Events_SeriesId_ScheduledStartUtc idempotency boundary
/// under real concurrent commits, narrow error mapping, UTC round-trips and RowVersion conflicts.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class EventSeriesSqlServerIntegrationTests : IAsyncLifetime
{
    private const int UniqueIndexViolation = 2601;
    private const int CheckConstraintViolation = 547;

    // Monday 2026-10-05 12:00 UTC.
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly NextTuesday = new(2026, 10, 13);

    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public EventSeriesSqlServerIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "eventseries");
        await _db.MigrateToAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Create_PersistsSeriesAndInitialOccurrences_WithExactUtcInstants()
    {
        var hostId = await AddPlayerAsync();

        var created = await CreateAsync(hostId, Body("FREQ=WEEKLY;BYDAY=TU", startDate: new DateOnly(2026, 10, 27)));

        await using var context = _db.CreateContext();
        var series = await context.EventSeries.AsNoTracking().SingleAsync(s => s.Id == created.Id);
        series.MaxParticipants.Should().Be(16);
        series.TimeZoneId.Should().Be("America/New_York");
        var occurrences = await context.Events.AsNoTracking()
            .Where(e => e.SeriesId == created.Id)
            .OrderBy(e => e.ScheduledStartUtc)
            .ToListAsync();

        // 10-27 .. 11-16: three Tuesdays at 17:00 local, across the 11-01 fall-back.
        occurrences.Select(o => o.ScheduledStartUtc.Ticks).Should().Equal(
            new DateTime(2026, 10, 27, 21, 0, 0).Ticks,
            new DateTime(2026, 11, 3, 22, 0, 0).Ticks,
            new DateTime(2026, 11, 10, 22, 0, 0).Ticks);
        occurrences.Should().OnlyContain(o =>
            o.ScheduledEndUtc!.Value.Ticks == o.ScheduledStartUtc.AddMinutes(75).Ticks &&
            o.HostId == hostId && o.MaxParticipants == 16 && o.Status == EventStatus.Planned && !o.IsDetached);
        occurrences.Select(o => o.OccurrenceIndex).Should().Equal(0, 1, 2);

        // datetime2 drops DateTime.Kind; the API layer marks values read back as UTC.
        occurrences[0].ScheduledStartUtc.Kind.Should().Be(DateTimeKind.Unspecified);
        var page = await new EventSeriesService(_db.CreateContext(), new FixedTimeProvider(Now))
            .GetOccurrencesAsync(created.Id, 1, 20);
        page!.Items.Should().OnlyContain(o =>
            o.ScheduledStartUtc.Kind == DateTimeKind.Utc && o.ScheduledEndUtc!.Value.Kind == DateTimeKind.Utc);
        page.Items.First().ScheduledStartUtc.Should().Be(new DateTime(2026, 10, 27, 21, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Create_IsAtomic_WhenAnOccurrenceInsertFails()
    {
        // A test-only CHECK that rejects the occurrence rows (never the series row).
        await ExecuteAsync("ALTER TABLE [Events] ADD CONSTRAINT [CK_Test_NoBoom] CHECK ([Name] <> N'boom')");
        var hostId = await AddPlayerAsync();

        var act = () => CreateAsync(hostId, Body("FREQ=DAILY", name: "boom"));

        var exception = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        exception.InnerException.Should().BeOfType<SqlException>().Which.Number.Should().Be(CheckConstraintViolation);
        await using var context = _db.CreateContext();
        (await context.EventSeries.CountAsync()).Should().Be(0);
        (await context.Events.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Materialize_Repeated_CreatesNoDuplicates_AndExtendsWithStableIndices()
    {
        var created = await CreateAsync(await AddPlayerAsync(), Body("FREQ=DAILY"));
        var materializer = new EventSeriesMaterializer(_db.CreateContext());

        (await materializer.MaterializeAsync(created.Id, NextTuesday.AddDays(20))).Should().Be(0);
        (await materializer.MaterializeAsync(created.Id, NextTuesday.AddDays(5))).Should().Be(0);
        (await new EventSeriesMaterializer(_db.CreateContext()).MaterializeAsync(created.Id, NextTuesday.AddDays(27))).Should().Be(7);
        (await new EventSeriesMaterializer(_db.CreateContext()).MaterializeAsync(created.Id, NextTuesday.AddDays(27))).Should().Be(0);

        await using var context = _db.CreateContext();
        var occurrences = await context.Events.AsNoTracking().Where(e => e.SeriesId == created.Id)
            .OrderBy(e => e.ScheduledStartUtc).ToListAsync();
        occurrences.Should().HaveCount(28);
        occurrences.Select(o => o.OccurrenceIndex).Should().Equal(Enumerable.Range(0, 28).Cast<int?>());
        occurrences.Select(o => o.ScheduledStartUtc).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Materialize_DoesNotRegenerateADetachedOccurrenceThatWasMoved()
    {
        var created = await CreateAsync(await AddPlayerAsync(), Body("FREQ=DAILY"));
        await ExecuteAsync($"""
            UPDATE [Events] SET [IsDetached] = 1, [ScheduledStartUtc] = DATEADD(hour, 2, [ScheduledStartUtc])
            WHERE [SeriesId] = '{created.Id}' AND [OccurrenceIndex] = 3
            """);

        (await new EventSeriesMaterializer(_db.CreateContext()).MaterializeAsync(created.Id, NextTuesday.AddDays(20))).Should().Be(0);

        await using var context = _db.CreateContext();
        (await context.Events.CountAsync(e => e.SeriesId == created.Id)).Should().Be(21);
    }

    [Fact]
    public async Task Materialize_Concurrent_CreatesNoDuplicates_AndTheLoserSucceedsIdempotently()
    {
        var seriesId = await AddBareSeriesAsync();
        var through = NextTuesday.AddDays(13);
        var winnerInserted = -1;

        await using var loserContext = new InterferingTeamBuilderDbContext(Options(), async () =>
        {
            // The loser has already read "nothing exists" when the winner commits.
            winnerInserted = await new EventSeriesMaterializer(_db.CreateContext()).MaterializeAsync(seriesId, through);
        });

        var loserInserted = await new EventSeriesMaterializer(loserContext).MaterializeAsync(seriesId, through);

        winnerInserted.Should().Be(14);
        loserInserted.Should().Be(0);
        loserContext.ChangeTracker.Entries<EventOccurrence>().Should().BeEmpty();
        await using var context = _db.CreateContext();
        var starts = await context.Events.Where(e => e.SeriesId == seriesId).Select(e => e.ScheduledStartUtc).ToListAsync();
        starts.Should().HaveCount(14).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Materialize_UnrelatedDatabaseErrors_AreNotSwallowed()
    {
        var seriesId = await AddBareSeriesAsync(name: "boom");
        await ExecuteAsync("ALTER TABLE [Events] ADD CONSTRAINT [CK_Test_NoBoom] CHECK ([Name] <> N'boom')");

        var act = () => new EventSeriesMaterializer(_db.CreateContext()).MaterializeAsync(seriesId, NextTuesday.AddDays(3));

        var exception = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        exception.InnerException.Should().BeOfType<SqlException>().Which.Number.Should().Be(CheckConstraintViolation);
        EventSeriesOccurrenceConflictClassifier.IsDuplicateSeriesOccurrence(exception).Should().BeFalse();
    }

    [Fact]
    public async Task Materialize_NonActiveOrMissingSeries_InsertsNothing()
    {
        var seriesId = await AddBareSeriesAsync(status: EventSeriesStatus.Cancelled);

        (await new EventSeriesMaterializer(_db.CreateContext()).MaterializeAsync(seriesId, NextTuesday.AddDays(20))).Should().Be(0);
        (await new EventSeriesMaterializer(_db.CreateContext()).MaterializeAsync(Guid.NewGuid(), NextTuesday.AddDays(20))).Should().Be(0);
        await using var context = _db.CreateContext();
        (await context.Events.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UniqueIndex_RejectsSecondOccurrenceOfASeriesAtTheSameInstant()
    {
        var seriesId = await AddBareSeriesAsync();
        var start = new DateTime(2026, 10, 13, 21, 0, 0, DateTimeKind.Utc);
        await AddOccurrenceAsync(seriesId, start);

        var act = () => AddOccurrenceAsync(seriesId, start);

        var exception = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        var sql = exception.InnerException.Should().BeOfType<SqlException>().Which;
        sql.Number.Should().Be(UniqueIndexViolation);
        sql.Message.Should().Contain("UX_Events_SeriesId_ScheduledStartUtc");
        EventSeriesOccurrenceConflictClassifier.IsDuplicateSeriesOccurrence(exception).Should().BeTrue();

        // Same instant in a different series is fine.
        await AddOccurrenceAsync(await AddBareSeriesAsync(), start);
    }

    [Fact]
    public async Task UniqueIndex_DoesNotAffectOneOffOccurrences()
    {
        var start = new DateTime(2026, 10, 13, 21, 0, 0, DateTimeKind.Utc);

        await AddOccurrenceAsync(null, start);
        await AddOccurrenceAsync(null, start);

        await using var context = _db.CreateContext();
        (await context.Events.CountAsync(e => e.SeriesId == null && e.ScheduledStartUtc == start)).Should().Be(2);
    }

    [Fact]
    public async Task Cancel_CancelsOnlyFutureAttachedOccurrences_AndKeepsAllRows()
    {
        // Daily 09:00 UTC from today: today's occurrence is already past at 12:00Z.
        var created = await CreateAsync(await AddPlayerAsync(), Body("FREQ=DAILY", startDate: new DateOnly(2026, 10, 5), timeZoneId: "UTC", time: new TimeOnly(9, 0)));
        await ExecuteAsync($"UPDATE [Events] SET [IsDetached] = 1 WHERE [SeriesId] = '{created.Id}' AND [OccurrenceIndex] = 2");

        (await Service().CancelAsync(created.Id)).Should().BeTrue();

        await using var context = _db.CreateContext();
        (await context.EventSeries.AsNoTracking().SingleAsync(s => s.Id == created.Id)).Status.Should().Be(EventSeriesStatus.Cancelled);
        var occurrences = await context.Events.AsNoTracking().Where(e => e.SeriesId == created.Id).ToDictionaryAsync(e => e.OccurrenceIndex!.Value);
        occurrences.Should().HaveCount(21);
        occurrences[0].Status.Should().Be(EventStatus.Planned);  // past
        occurrences[2].Status.Should().Be(EventStatus.Planned);  // detached
        occurrences.Where(kv => kv.Key is not (0 or 2)).Should().OnlyContain(kv => kv.Value.Status == EventStatus.Cancelled);
    }

    [Fact]
    public async Task Cancel_Concurrent_LoserGetsDeterministicConflict()
    {
        var created = await CreateAsync(await AddPlayerAsync(), Body("FREQ=DAILY"));

        await using var loserContext = new InterferingTeamBuilderDbContext(Options(), async () =>
        {
            (await Service().CancelAsync(created.Id)).Should().BeTrue();
        });
        var loser = new EventSeriesService(loserContext, new FixedTimeProvider(Now));

        var act = () => loser.CancelAsync(created.Id);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.InnerException
            .Should().BeOfType<DbUpdateConcurrencyException>();
        await using var context = _db.CreateContext();
        (await context.EventSeries.AsNoTracking().SingleAsync(s => s.Id == created.Id)).Status.Should().Be(EventSeriesStatus.Cancelled);
        (await context.Events.CountAsync(e => e.SeriesId == created.Id && e.Status == EventStatus.Cancelled)).Should().Be(21);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private EventSeriesService Service() => new(_db.CreateContext(), new FixedTimeProvider(Now));

    private Task<EventSeriesDto> CreateAsync(Guid hostId, CreateEventSeriesDto body) => Service().CreateAsync(body, hostId);

    private static CreateEventSeriesDto Body(
        string rrule,
        DateOnly? startDate = null,
        string name = "Weekly game",
        string timeZoneId = "America/New_York",
        TimeOnly? time = null) => new()
    {
        Name = name,
        RecurrenceRule = rrule,
        TimeZoneId = timeZoneId,
        LocalStartTime = time ?? new TimeOnly(17, 0),
        DurationMinutes = 75,
        SeriesStartDate = startDate ?? NextTuesday,
        MaxParticipants = 16
    };

    private DbContextOptions<TeamBuilderDbContext> Options() =>
        new DbContextOptionsBuilder<TeamBuilderDbContext>().UseSqlServer(_db.ConnectionString).Options;

    private async Task<Guid> AddPlayerAsync()
    {
        await using var context = _db.CreateContext();
        var player = new Player { Id = Guid.NewGuid(), Username = $"host_{Guid.NewGuid():N}" };
        context.Players.Add(player);
        await context.SaveChangesAsync();
        return player.Id;
    }

    /// <summary>A series row with no occurrences, as a later rolling worker would find it.</summary>
    private async Task<Guid> AddBareSeriesAsync(string name = "Bare series", EventSeriesStatus status = EventSeriesStatus.Active)
    {
        await using var context = _db.CreateContext();
        var series = SeriesSchedulingTests.NewSeries("America/New_York", "FREQ=DAILY", NextTuesday, new TimeOnly(17, 0));
        series.Name = name;
        series.Status = status;
        context.EventSeries.Add(series);
        await context.SaveChangesAsync();
        return series.Id;
    }

    private async Task AddOccurrenceAsync(Guid? seriesId, DateTime start)
    {
        await using var context = _db.CreateContext();
        context.Events.Add(new EventOccurrence
        {
            Id = Guid.NewGuid(),
            Name = "Occurrence",
            SeriesId = seriesId,
            ScheduledStartUtc = start,
            Status = EventStatus.Planned,
            MaxParticipants = 10
        });
        await context.SaveChangesAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Runs a real, independently committed write immediately before this context's first
    /// SaveChanges, after the code under test has already read its state.
    /// </summary>
    private sealed class InterferingTeamBuilderDbContext(
        DbContextOptions<TeamBuilderDbContext> options,
        Func<Task> beforeFirstSave) : TeamBuilderDbContext(options)
    {
        private Func<Task>? _beforeFirstSave = beforeFirstSave;

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var interference = _beforeFirstSave;
            _beforeFirstSave = null;
            if (interference is not null)
                await interference();

            return await base.SaveChangesAsync(cancellationToken);
        }
    }
}
