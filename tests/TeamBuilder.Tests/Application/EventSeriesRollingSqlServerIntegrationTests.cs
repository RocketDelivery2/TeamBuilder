using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamBuilder.Api.Workers;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Persistence;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Rolling materialization on a real, fully migrated SQL Server database: checkpoint and
/// occurrences commit atomically, the RowVersion-checked series UPDATE serializes materialization
/// against cancellation and against other materializers, and concurrent workers leave exactly one
/// occurrence per recurrence. Races use an interfering DbContext that commits a real competing
/// write after the code under test has read its state, or genuinely parallel tasks.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class EventSeriesRollingSqlServerIntegrationTests : IAsyncLifetime
{
    private const int CheckConstraintViolation = 547;

    // Monday 2026-10-05 12:00 UTC (08:00 in New York).
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 5);

    private readonly SqlServerContainerFixture _fixture;
    private readonly FixedTimeProvider _clock = new(Now);
    private SqlServerTestDatabase _db = null!;

    public EventSeriesRollingSqlServerIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "rolling");
        await _db.MigrateToAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ── checkpoint ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_PersistsTheCheckpoint_InTheSameTransaction()
    {
        var created = await CreateAsync("FREQ=DAILY");
        var bounded = await CreateAsync("FREQ=DAILY", endDate: Today.AddDays(6));

        (await CheckpointAsync(created.Id)).Should().Be(Today.AddDays(20));
        (await CheckpointAsync(bounded.Id)).Should().Be(Today.AddDays(6));
        (await ScalarAsync<string>("SELECT TYPE_NAME([system_type_id]) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[EventSeries]') AND [name] = N'MaterializedThroughLocalDate'"))
            .Should().Be("date");
    }

    [Fact]
    public async Task UnknownCheckpoint_RecoversASeriesCreatedBeforeCheckpoints_WithoutDuplicates()
    {
        var created = await CreateAsync("FREQ=WEEKLY;BYDAY=MO,TU,TH");
        await ExecuteAsync($"UPDATE [EventSeries] SET [MaterializedThroughLocalDate] = NULL WHERE [Id] = '{created.Id}'");
        var before = await OccurrencesAsync(created.Id);

        _clock.UtcNow = Now.AddDays(4);
        var result = await Materializer().MaintainHorizonAsync(created.Id);

        result.Outcome.Should().Be(EventSeriesMaterializationOutcome.Materialized);
        result.CheckpointAfter.Should().Be(Today.AddDays(24));
        var after = await OccurrencesAsync(created.Id);
        after.Select(o => o.ScheduledStartUtc).Should().OnlyHaveUniqueItems();
        after.Select(o => o.OccurrenceIndex).Should().Equal(Enumerable.Range(0, after.Count).Cast<int?>());
        after.Take(before.Count).Select(o => o.Id).Should().Equal(before.Select(o => o.Id));
        (await CheckpointAsync(created.Id)).Should().Be(Today.AddDays(24));
    }

    [Fact]
    public async Task FailedOccurrencePersistence_DoesNotAdvanceTheCheckpoint_AndPropagates()
    {
        var created = await CreateAsync("FREQ=DAILY");
        // A test-only CHECK that rejects only occurrences after the initial window.
        await ExecuteAsync("ALTER TABLE [Events] ADD CONSTRAINT [CK_Test_NoLateRows] CHECK ([ScheduledStartUtc] < '2026-10-26T12:00:00')");

        _clock.UtcNow = Now.AddDays(3);
        var act = () => Materializer().MaintainHorizonAsync(created.Id);

        var exception = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        exception.InnerException.Should().BeOfType<SqlException>().Which.Number.Should().Be(CheckConstraintViolation);
        EventSeriesOccurrenceConflictClassifier.IsDuplicateSeriesOccurrence(exception).Should().BeFalse();
        (await CheckpointAsync(created.Id)).Should().Be(Today.AddDays(20));
        (await CountAsync(created.Id)).Should().Be(21);
    }

    // ── RowVersion / concurrent materializers ────────────────────────────────

    [Fact]
    public async Task ConcurrentCheckpointAdvance_LoserCommitsNothing_AndReportsUpToDate()
    {
        var created = await CreateAsync("FREQ=DAILY");
        _clock.UtcNow = Now.AddDays(5);
        EventSeriesMaterializationResult? winner = null;

        await using var loserContext = new InterferingTeamBuilderDbContext(Options(), async () =>
        {
            winner = await Materializer().MaintainHorizonAsync(created.Id);
        });

        var loser = await new EventSeriesMaterializer(loserContext, _clock).MaintainHorizonAsync(created.Id);

        winner!.Inserted.Should().Be(5);
        loser.Outcome.Should().Be(EventSeriesMaterializationOutcome.UpToDate);
        loser.Inserted.Should().Be(0);
        loser.CheckpointAfter.Should().Be(Today.AddDays(25));
        loserContext.ChangeTracker.Entries().Should().BeEmpty();
        (await CountAsync(created.Id)).Should().Be(26);
        (await CheckpointAsync(created.Id)).Should().Be(Today.AddDays(25));
    }

    [Fact]
    public async Task ConcurrentShorterAdvance_LoserReportsConflict_AndTheNextPassCompletes()
    {
        var created = await CreateAsync("FREQ=DAILY");
        _clock.UtcNow = Now.AddDays(5);

        await using var loserContext = new InterferingTeamBuilderDbContext(Options(), async () =>
        {
            // Another instance with an older clock advances only part of the way.
            (await Materializer().MaterializeAsync(created.Id, Today.AddDays(21), Today.AddDays(22))).Inserted.Should().Be(2);
        });

        var loser = await new EventSeriesMaterializer(loserContext, _clock).MaintainHorizonAsync(created.Id);

        loser.Outcome.Should().Be(EventSeriesMaterializationOutcome.Conflict);
        loser.Inserted.Should().Be(0);
        (await CheckpointAsync(created.Id)).Should().Be(Today.AddDays(22));
        (await CountAsync(created.Id)).Should().Be(23);

        var next = await Materializer().MaintainHorizonAsync(created.Id);
        next.Inserted.Should().Be(3);
        (await CheckpointAsync(created.Id)).Should().Be(Today.AddDays(25));
    }

    [Fact]
    public async Task RowVersion_IsChecked_EvenWhenNoOccurrenceCollides()
    {
        var created = await CreateAsync("FREQ=DAILY");
        var rowVersionBefore = await RowVersionAsync(created.Id);
        _clock.UtcNow = Now.AddDays(2);

        await using var loserContext = new InterferingTeamBuilderDbContext(Options(), () =>
            // An unrelated committed change to the series row: only the RowVersion can catch it.
            ExecuteAsync($"UPDATE [EventSeries] SET [Description] = N'changed' WHERE [Id] = '{created.Id}'"));

        var loser = await new EventSeriesMaterializer(loserContext, _clock).MaintainHorizonAsync(created.Id);

        loser.Outcome.Should().Be(EventSeriesMaterializationOutcome.Conflict);
        (await CountAsync(created.Id)).Should().Be(21);
        (await CheckpointAsync(created.Id)).Should().Be(Today.AddDays(20));

        // A successful pass bumps the RowVersion (the checkpoint UPDATE is real).
        var rowVersionAfterInterference = await RowVersionAsync(created.Id);
        (await Materializer().MaintainHorizonAsync(created.Id)).Inserted.Should().Be(2);
        var rowVersionAfterPass = await RowVersionAsync(created.Id);
        rowVersionAfterInterference.Should().NotEqual(rowVersionBefore);
        rowVersionAfterPass.Should().NotEqual(rowVersionAfterInterference);
    }

    [Fact]
    public async Task ParallelMaterializers_LeaveExactlyOneOccurrencePerRecurrence()
    {
        var seriesId = await AddBareSeriesAsync("FREQ=WEEKLY;BYDAY=MO,WE,FR,SU");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Materializer().MaintainHorizonAsync(seriesId))));

        results.Sum(r => r.Inserted).Should().Be(12);
        results.Should().OnlyContain(r => r.Outcome == EventSeriesMaterializationOutcome.Materialized ||
                                          r.Outcome == EventSeriesMaterializationOutcome.UpToDate ||
                                          r.Outcome == EventSeriesMaterializationOutcome.Conflict);
        var occurrences = await OccurrencesAsync(seriesId);
        occurrences.Should().HaveCount(12);
        occurrences.Select(o => o.ScheduledStartUtc).Should().OnlyHaveUniqueItems();
        occurrences.Select(o => o.OccurrenceIndex).Should().Equal(Enumerable.Range(0, 12).Cast<int?>());
        (await CheckpointAsync(seriesId)).Should().Be(Today.AddDays(20));
    }

    [Fact]
    public async Task ParallelWorkers_LeaveExactlyOneOccurrencePerRecurrence_ForEverySeries()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 7; i++)
            ids.Add(await AddBareSeriesAsync(i % 2 == 0 ? "FREQ=DAILY" : "FREQ=WEEKLY;BYDAY=TU,SA"));
        await using var providerA = WorkerServices();
        await using var providerB = WorkerServices();

        var summaries = await Task.WhenAll(
            Task.Run(() => Worker(providerA, batchSize: 3).RunPassAsync(CancellationToken.None)),
            Task.Run(() => Worker(providerB, batchSize: 2).RunPassAsync(CancellationToken.None)));

        summaries.Should().OnlyContain(s => s.Failed == 0 && s.Processed == 7);
        summaries.Sum(s => s.Inserted).Should().Be(4 * 21 + 3 * 6);
        foreach (var id in ids)
        {
            var occurrences = await OccurrencesAsync(id);
            occurrences.Select(o => o.ScheduledStartUtc).Should().OnlyHaveUniqueItems();
            occurrences.Select(o => o.OccurrenceIndex).Should().Equal(Enumerable.Range(0, occurrences.Count).Cast<int?>());
            (await CheckpointAsync(id)).Should().Be(Today.AddDays(20));
        }
    }

    // ── cancel vs materialize ────────────────────────────────────────────────

    [Fact]
    public async Task CancellationCommittedBeforeWorkerCommit_WorkerCommitsNothing()
    {
        var created = await CreateAsync("FREQ=DAILY");
        _clock.UtcNow = Now.AddDays(4);

        // The worker has read the series as Active (V1) and computed its rows when the
        // cancellation commits (V2).
        await using var workerContext = new InterferingTeamBuilderDbContext(Options(), async () =>
        {
            (await new EventSeriesService(_db.CreateContext(), _clock).CancelAsync(created.Id)).Should().BeTrue();
        });

        var result = await new EventSeriesMaterializer(workerContext, _clock).MaintainHorizonAsync(created.Id);

        result.Outcome.Should().Be(EventSeriesMaterializationOutcome.NotActive);
        result.Inserted.Should().Be(0);
        (await CountAsync(created.Id)).Should().Be(21);
        await using var context = _db.CreateContext();
        (await context.Events.CountAsync(e => e.SeriesId == created.Id && e.Status == EventStatus.Planned &&
                                              e.ScheduledStartUtc > _clock.UtcNow.UtcDateTime)).Should().Be(0);
        (await CheckpointAsync(created.Id)).Should().Be(Today.AddDays(20));
    }

    [Fact]
    public async Task WorkerCommittedFirst_LaterCancellationCancelsTheNewOccurrences_ExceptDetached()
    {
        var created = await CreateAsync("FREQ=DAILY");
        _clock.UtcNow = Now.AddDays(4);
        (await Materializer().MaintainHorizonAsync(created.Id)).Inserted.Should().Be(4);
        await ExecuteAsync($"UPDATE [Events] SET [IsDetached] = 1 WHERE [SeriesId] = '{created.Id}' AND [OccurrenceIndex] = 23");

        (await new EventSeriesService(_db.CreateContext(), _clock).CancelAsync(created.Id)).Should().BeTrue();

        var occurrences = (await OccurrencesAsync(created.Id)).ToDictionary(o => o.OccurrenceIndex!.Value);
        occurrences.Should().HaveCount(25);
        occurrences[23].Status.Should().Be(EventStatus.Planned);
        occurrences.Where(kv => kv.Key >= 4 && kv.Key != 23).Should().OnlyContain(kv => kv.Value.Status == EventStatus.Cancelled);
        occurrences.Where(kv => kv.Key < 4).Should().OnlyContain(kv => kv.Value.Status == EventStatus.Planned); // past
    }

    [Fact]
    public async Task WorkerCommittingDuringCancellation_CancellationLosesDeterministically_AndRetrySucceeds()
    {
        var created = await CreateAsync("FREQ=DAILY");
        _clock.UtcNow = Now.AddDays(4);

        // The cancellation has read the future occurrences when the worker commits new ones.
        await using var cancelContext = new InterferingTeamBuilderDbContext(Options(), async () =>
        {
            (await Materializer().MaintainHorizonAsync(created.Id)).Inserted.Should().Be(4);
        });

        var act = () => new EventSeriesService(cancelContext, _clock).CancelAsync(created.Id);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.InnerException.Should().BeOfType<DbUpdateConcurrencyException>();
        (await StatusAsync(created.Id)).Should().Be(EventSeriesStatus.Active);

        (await new EventSeriesService(_db.CreateContext(), _clock).CancelAsync(created.Id)).Should().BeTrue();
        await using var context = _db.CreateContext();
        (await context.Events.CountAsync(e => e.SeriesId == created.Id && e.Status == EventStatus.Planned &&
                                              e.ScheduledStartUtc > _clock.UtcNow.UtcDateTime)).Should().Be(0);
    }

    // ── migration ────────────────────────────────────────────────────────────

    [Fact]
    public async Task FullyMigratedDatabase_HasNoPendingModelChanges()
    {
        await using var context = _db.CreateContext();
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private EventSeriesMaterializer Materializer() => new(_db.CreateContext(), _clock);

    private DbContextOptions<TeamBuilderDbContext> Options() =>
        new DbContextOptionsBuilder<TeamBuilderDbContext>().UseSqlServer(_db.ConnectionString).Options;

    private ServiceProvider WorkerServices()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TeamBuilderDbContext>(options => options.UseSqlServer(_db.ConnectionString));
        services.AddSingleton<TimeProvider>(_clock);
        services.AddScoped<IEventSeriesMaterializer, EventSeriesMaterializer>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private EventSeriesMaterializationWorker Worker(ServiceProvider provider, int batchSize) => new(
        provider.GetRequiredService<IServiceScopeFactory>(),
        _clock,
        Microsoft.Extensions.Options.Options.Create(new EventSeriesMaterializationOptions { BatchSize = batchSize }),
        NullLogger<EventSeriesMaterializationWorker>.Instance);

    private async Task<EventSeriesDto> CreateAsync(string rrule, DateOnly? endDate = null) =>
        await new EventSeriesService(_db.CreateContext(), _clock).CreateAsync(new CreateEventSeriesDto
        {
            Name = "Pickup",
            RecurrenceRule = rrule,
            TimeZoneId = "America/New_York",
            LocalStartTime = new TimeOnly(17, 0),
            DurationMinutes = 60,
            SeriesStartDate = Today,
            SeriesEndDate = endDate,
            MaxParticipants = 10
        }, await AddPlayerAsync());

    private async Task<Guid> AddPlayerAsync()
    {
        await using var context = _db.CreateContext();
        var player = new Player { Id = Guid.NewGuid(), Username = $"host_{Guid.NewGuid():N}" };
        context.Players.Add(player);
        await context.SaveChangesAsync();
        return player.Id;
    }

    /// <summary>An Active series with no occurrences and no checkpoint.</summary>
    private async Task<Guid> AddBareSeriesAsync(string rrule)
    {
        await using var context = _db.CreateContext();
        var series = SeriesSchedulingTests.NewSeries("America/New_York", rrule, Today, new TimeOnly(17, 0));
        context.EventSeries.Add(series);
        await context.SaveChangesAsync();
        return series.Id;
    }

    private async Task<List<EventOccurrence>> OccurrencesAsync(Guid seriesId)
    {
        await using var context = _db.CreateContext();
        return await context.Events.AsNoTracking().Where(e => e.SeriesId == seriesId).OrderBy(e => e.ScheduledStartUtc).ToListAsync();
    }

    private async Task<int> CountAsync(Guid seriesId)
    {
        await using var context = _db.CreateContext();
        return await context.Events.CountAsync(e => e.SeriesId == seriesId);
    }

    private async Task<DateOnly?> CheckpointAsync(Guid seriesId)
    {
        await using var context = _db.CreateContext();
        return await context.EventSeries.Where(s => s.Id == seriesId).Select(s => s.MaterializedThroughLocalDate).SingleAsync();
    }

    private async Task<EventSeriesStatus> StatusAsync(Guid seriesId)
    {
        await using var context = _db.CreateContext();
        return await context.EventSeries.Where(s => s.Id == seriesId).Select(s => s.Status).SingleAsync();
    }

    private async Task<byte[]> RowVersionAsync(Guid seriesId)
    {
        await using var context = _db.CreateContext();
        return await context.EventSeries.Where(s => s.Id == seriesId).Select(s => s.RowVersion).SingleAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
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
