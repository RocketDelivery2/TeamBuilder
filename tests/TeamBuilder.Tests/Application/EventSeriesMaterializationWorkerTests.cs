using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamBuilder.Api.Workers;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// The hosted worker: startup pass, per-series scopes, failure isolation, deterministic keyset
/// batching, clean shutdown, and (with the real materializer) keeping the horizon rolling.
/// </summary>
public class EventSeriesMaterializationWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 5);

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly FixedTimeProvider _clock = new(Now);

    [Fact]
    public void DefaultCadence_IsHourly_WithBatchesOf100()
    {
        var options = new EventSeriesMaterializationOptions();

        options.Interval.Should().Be(TimeSpan.FromHours(1));
        options.BatchSize.Should().Be(100);
        options.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Worker_PerformsAPassOnStartup_WithoutWaitingAnInterval_AndStopsCleanly()
    {
        var seriesId = await AddSeriesAsync();
        var recorder = new RecordingMaterializer();
        await using var provider = Services(recorder);
        var worker = Worker(provider, new EventSeriesMaterializationOptions { Interval = TimeSpan.FromHours(1) });

        await worker.StartAsync(CancellationToken.None);
        var first = await recorder.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(30));

        first.Should().Be(seriesId);
        var stop = () => worker.StopAsync(CancellationToken.None);
        await stop.Should().NotThrowAsync();
        worker.ExecuteTask!.IsCompleted.Should().BeTrue();
        recorder.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Worker_Disabled_DoesNothing()
    {
        await AddSeriesAsync();
        var recorder = new RecordingMaterializer();
        await using var provider = Services(recorder);
        var worker = Worker(provider, new EventSeriesMaterializationOptions { Enabled = false });

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
        await worker.StopAsync(CancellationToken.None);

        recorder.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Pass_UsesAFreshScopeForEverySeries()
    {
        for (var i = 0; i < 3; i++)
            await AddSeriesAsync();
        var recorder = new RecordingMaterializer();
        await using var provider = Services(recorder);

        await Worker(provider).RunPassAsync(CancellationToken.None);

        recorder.Instances.Should().HaveCount(3).And.OnlyHaveUniqueItems();
        recorder.Contexts.Should().HaveCount(3).And.OnlyHaveUniqueItems();
        recorder.Contexts.Should().OnlyContain(c => c.Disposed);
    }

    [Fact]
    public async Task Pass_FailureInOneSeries_DoesNotBlockTheNext_AndIsLoggedWithTheSeriesId()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
            ids.Add(await AddSeriesAsync());
        var ordered = ids.Order().ToList();
        var recorder = new RecordingMaterializer { FailFor = ordered[1] };
        var logger = new ListLogger();
        await using var provider = Services(recorder);

        var summary = await Worker(provider, logger: logger).RunPassAsync(CancellationToken.None);

        recorder.Calls.Should().Equal(ordered);
        summary.Processed.Should().Be(4);
        summary.Failed.Should().Be(1);
        logger.Errors.Should().ContainSingle().Which.Should().Contain(ordered[1].ToString());
    }

    [Fact]
    public async Task Pass_BatchesActiveSeriesDeterministically_ByIdKeyset()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
            ids.Add(await AddSeriesAsync());
        await AddSeriesAsync(EventSeriesStatus.Cancelled);
        await AddSeriesAsync(EventSeriesStatus.Paused);
        await AddSeriesAsync(EventSeriesStatus.Completed);
        var recorder = new RecordingMaterializer();
        await using var provider = Services(recorder);

        var summary = await Worker(provider, new EventSeriesMaterializationOptions { BatchSize = 2 }).RunPassAsync(CancellationToken.None);

        recorder.Calls.Should().Equal(ids.Order());
        summary.Batches.Should().Be(3);
        summary.Processed.Should().Be(5);

        // A second pass sees the same order.
        recorder.Calls.Clear();
        await Worker(provider, new EventSeriesMaterializationOptions { BatchSize = 2 }).RunPassAsync(CancellationToken.None);
        recorder.Calls.Should().Equal(ids.Order());
    }

    [Fact]
    public async Task Pass_SkipsSeriesThatCannotNeedWork()
    {
        var candidate = await AddSeriesAsync(checkpoint: Today.AddDays(20));
        await AddSeriesAsync(checkpoint: Today.AddDays(21)); // beyond any zone's target today
        await AddSeriesAsync(endDate: Today.AddDays(-2)); // ended in every zone
        var recorder = new RecordingMaterializer();
        await using var provider = Services(recorder);

        await Worker(provider).RunPassAsync(CancellationToken.None);

        recorder.Calls.Should().Equal(candidate);
    }

    [Fact]
    public async Task Pass_ShutdownCancellation_IsNotSwallowedAsASeriesFailure()
    {
        await AddSeriesAsync();
        await AddSeriesAsync();
        using var cts = new CancellationTokenSource();
        var recorder = new RecordingMaterializer { OnCall = () => cts.Cancel() };
        var logger = new ListLogger();
        await using var provider = Services(recorder);

        var act = () => Worker(provider, logger: logger).RunPassAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        recorder.Calls.Should().ContainSingle();
        logger.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Pass_WithTheRealMaterializer_KeepsDailyAndWeeklySeriesTwentyOneDaysAhead()
    {
        var daily = await CreateAsync("FREQ=DAILY");
        var weekly = await CreateAsync("FREQ=WEEKLY;BYDAY=TU");
        await using var provider = Services(null);

        _clock.UtcNow = Now.AddDays(7);
        var summary = await Worker(provider).RunPassAsync(CancellationToken.None);

        summary.Failed.Should().Be(0);
        summary.Inserted.Should().Be(7 + 1);
        await using var context = Context();
        var dailyRows = await context.Events.Where(e => e.SeriesId == daily.Id).ToListAsync();
        dailyRows.Should().HaveCount(28);
        dailyRows.Count(e => e.ScheduledStartUtc >= new DateTime(2026, 10, 12)).Should().Be(21);
        (await context.Events.CountAsync(e => e.SeriesId == weekly.Id)).Should().Be(4);
        (await context.EventSeries.SingleAsync(s => s.Id == daily.Id)).MaterializedThroughLocalDate.Should().Be(Today.AddDays(27));

        var again = await Worker(provider).RunPassAsync(CancellationToken.None);
        again.Inserted.Should().Be(0);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private TeamBuilderDbContext Context() =>
        new(new DbContextOptionsBuilder<TeamBuilderDbContext>().UseInMemoryDatabase(_databaseName).Options);

    private ServiceProvider Services(RecordingMaterializer? recorder)
    {
        var services = new ServiceCollection();
        services.AddDbContext<TeamBuilderDbContext>(options => options.UseInMemoryDatabase(_databaseName));
        services.AddSingleton<TimeProvider>(_clock);
        if (recorder is null)
        {
            services.AddScoped<IEventSeriesMaterializer, EventSeriesMaterializer>();
        }
        else
        {
            services.AddScoped<ScopeProbe>();
            services.AddScoped<IEventSeriesMaterializer>(sp => recorder.Create(sp.GetRequiredService<ScopeProbe>()));
        }

        return services.BuildServiceProvider(validateScopes: true);
    }

    private EventSeriesMaterializationWorker Worker(
        ServiceProvider provider,
        EventSeriesMaterializationOptions? options = null,
        ILogger<EventSeriesMaterializationWorker>? logger = null) => new(
        provider.GetRequiredService<IServiceScopeFactory>(),
        _clock,
        Options.Create(options ?? new EventSeriesMaterializationOptions()),
        logger ?? NullLogger<EventSeriesMaterializationWorker>.Instance);

    private async Task<Guid> AddSeriesAsync(
        EventSeriesStatus status = EventSeriesStatus.Active,
        DateOnly? checkpoint = null,
        DateOnly? endDate = null)
    {
        await using var context = Context();
        var series = SeriesSchedulingTests.NewSeries("America/New_York", "FREQ=DAILY", Today.AddDays(-10), new TimeOnly(17, 0));
        series.Status = status;
        series.MaterializedThroughLocalDate = checkpoint;
        series.SeriesEndDate = endDate;
        context.EventSeries.Add(series);
        await context.SaveChangesAsync();
        return series.Id;
    }

    private Task<EventSeriesDto> CreateAsync(string rrule) =>
        new EventSeriesService(Context(), _clock).CreateAsync(new CreateEventSeriesDto
        {
            Name = "Pickup",
            RecurrenceRule = rrule,
            TimeZoneId = "America/New_York",
            LocalStartTime = new TimeOnly(17, 0),
            DurationMinutes = 60,
            SeriesStartDate = Today,
            MaxParticipants = 10
        }, Guid.NewGuid());

    /// <summary>A scoped dependency whose disposal shows the worker disposed the scope.</summary>
    private sealed class ScopeProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingMaterializer
    {
        public List<Guid> Calls { get; } = [];
        public List<object> Instances { get; } = [];
        public List<ScopeProbe> Contexts { get; } = [];
        public TaskCompletionSource<Guid> FirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid? FailFor { get; init; }
        public Action? OnCall { get; init; }

        public IEventSeriesMaterializer Create(ScopeProbe probe)
        {
            var instance = new Instance(this, probe);
            Instances.Add(instance);
            Contexts.Add(probe);
            return instance;
        }

        private sealed class Instance(RecordingMaterializer owner, ScopeProbe probe) : IEventSeriesMaterializer
        {
            public Task<EventSeriesMaterializationResult> MaterializeAsync(Guid seriesId, DateOnly fromLocalDate, DateOnly throughLocalDate, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<EventSeriesMaterializationResult> MaintainHorizonAsync(Guid seriesId, CancellationToken cancellationToken = default)
            {
                probe.Disposed.Should().BeFalse();
                owner.Calls.Add(seriesId);
                owner.FirstCall.TrySetResult(seriesId);
                owner.OnCall?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                if (seriesId == owner.FailFor)
                    throw new InvalidOperationException("boom");
                return Task.FromResult(EventSeriesMaterializationResult.Nothing(EventSeriesMaterializationOutcome.UpToDate));
            }
        }
    }

    private sealed class ListLogger : ILogger<EventSeriesMaterializationWorker>
    {
        public ConcurrentQueue<string> Errors { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                Errors.Enqueue(formatter(state, exception) + " | " + exception?.Message);
        }
    }
}
