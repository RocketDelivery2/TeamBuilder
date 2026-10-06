using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Api.Workers;

/// <summary>
/// Keeps every Active recurring series materialized 21 local days ahead of today in its own time
/// zone. Runs one pass at startup, then one per <see cref="EventSeriesMaterializationOptions.Interval"/>.
/// Several application instances may run it at once: correctness comes from the idempotent
/// materializer, the UX_Events_SeriesId_ScheduledStartUtc unique index and the RowVersion-guarded
/// checkpoint update, not from a lock.
/// </summary>
public sealed class EventSeriesMaterializationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly EventSeriesMaterializationOptions _options;
    private readonly ILogger<EventSeriesMaterializationWorker> _logger;

    public EventSeriesMaterializationWorker(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<EventSeriesMaterializationOptions> options,
        ILogger<EventSeriesMaterializationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Event series materialization worker is disabled.");
            return;
        }

        while (true)
        {
            try
            {
                await RunPassAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // A pass-level failure (e.g. the database is unreachable while listing series)
                // must not stop the worker; the next pass retries.
                _logger.LogError(ex, "Event series materialization pass failed.");
            }

            // Shutdown cancels the delay; the OperationCanceledException ends ExecuteAsync.
            await Task.Delay(_options.Interval, _timeProvider, stoppingToken);
        }
    }

    /// <summary>
    /// One pass over all Active series, in keyset pages of <see cref="EventSeriesMaterializationOptions.BatchSize"/>
    /// ordered by Id. Each page read and each series uses its own DI scope (and DbContext), so a
    /// failure in one series cannot poison another.
    /// </summary>
    internal async Task<EventSeriesMaterializationPassSummary> RunPassAsync(CancellationToken cancellationToken)
    {
        var summary = new EventSeriesMaterializationPassSummary();
        Guid? lastId = null;

        while (true)
        {
            var ids = await ReadCandidateIdsAsync(lastId, cancellationToken);
            summary.Batches++;

            foreach (var seriesId in ids)
            {
                summary.Processed++;
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var materializer = scope.ServiceProvider.GetRequiredService<IEventSeriesMaterializer>();
                    var result = await materializer.MaintainHorizonAsync(seriesId, cancellationToken);
                    summary.Inserted += result.Inserted;
                    if (result.Outcome == EventSeriesMaterializationOutcome.Materialized)
                        summary.Materialized++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    summary.Failed++;
                    _logger.LogError(ex, "Materializing event series {SeriesId} failed; continuing with the next series.", seriesId);
                }
            }

            if (ids.Count < _options.BatchSize)
                break;
            lastId = ids[^1];
        }

        _logger.LogInformation(
            "Event series materialization pass: {Processed} series checked, {Materialized} advanced, {Inserted} occurrences inserted, {Failed} failed.",
            summary.Processed, summary.Materialized, summary.Inserted, summary.Failed);
        return summary;
    }

    private async Task<List<Guid>> ReadCandidateIdsAsync(Guid? lastId, CancellationToken cancellationToken)
    {
        // Conservative pre-filter: every time zone's local date is within a day of the UTC date,
        // so no series needs work when its checkpoint already reaches UTC today + 21 days, or when
        // it ended before UTC yesterday. The materializer applies the exact per-zone rules.
        var utcToday = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        var latestPossibleTarget = utcToday.AddDays(IEventSeriesMaterializer.RollingHorizonDays);
        var earliestPossibleToday = utcToday.AddDays(-1);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

        var query = context.EventSeries
            .AsNoTracking()
            .Where(s => s.Status == EventSeriesStatus.Active &&
                        (s.MaterializedThroughLocalDate == null || s.MaterializedThroughLocalDate < latestPossibleTarget) &&
                        (s.SeriesEndDate == null || s.SeriesEndDate >= earliestPossibleToday));
        if (lastId is { } after)
            query = query.Where(s => s.Id.CompareTo(after) > 0);

        return await query
            .OrderBy(s => s.Id)
            .Select(s => s.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);
    }
}

internal sealed class EventSeriesMaterializationPassSummary
{
    public int Batches { get; set; }
    public int Processed { get; set; }
    public int Materialized { get; set; }
    public int Inserted { get; set; }
    public int Failed { get; set; }
}
