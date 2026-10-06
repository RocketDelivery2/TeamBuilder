using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Scheduling;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Persistence;

namespace TeamBuilder.Infrastructure.Services;

public class EventSeriesMaterializer : IEventSeriesMaterializer
{
    private readonly TeamBuilderDbContext _context;
    private readonly TimeProvider _timeProvider;

    public EventSeriesMaterializer(TeamBuilderDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<EventSeriesMaterializationResult> MaterializeAsync(
        Guid seriesId,
        DateOnly fromLocalDate,
        DateOnly throughLocalDate,
        CancellationToken cancellationToken = default)
    {
        if (fromLocalDate > throughLocalDate)
            throw new ArgumentOutOfRangeException(nameof(fromLocalDate), "fromLocalDate must be on or before throughLocalDate.");

        var series = await _context.EventSeries.FirstOrDefaultAsync(s => s.Id == seriesId, cancellationToken);
        if (series == null)
            return EventSeriesMaterializationResult.Nothing(EventSeriesMaterializationOutcome.NotFound);
        if (series.Status != EventSeriesStatus.Active)
            return EventSeriesMaterializationResult.Nothing(EventSeriesMaterializationOutcome.NotActive, series.MaterializedThroughLocalDate);

        var (rule, timeZone) = Resolve(series);
        var today = SeriesLocalTime.Today(_timeProvider, timeZone);
        return await MaterializeRangeAsync(series, rule, timeZone, today, fromLocalDate, throughLocalDate, cancellationToken);
    }

    public async Task<EventSeriesMaterializationResult> MaintainHorizonAsync(Guid seriesId, CancellationToken cancellationToken = default)
    {
        var series = await _context.EventSeries.FirstOrDefaultAsync(s => s.Id == seriesId, cancellationToken);
        if (series == null)
            return EventSeriesMaterializationResult.Nothing(EventSeriesMaterializationOutcome.NotFound);

        var checkpoint = series.MaterializedThroughLocalDate;
        if (series.Status != EventSeriesStatus.Active)
            return EventSeriesMaterializationResult.Nothing(EventSeriesMaterializationOutcome.NotActive, checkpoint);

        var (rule, timeZone) = Resolve(series);
        var today = SeriesLocalTime.Today(_timeProvider, timeZone);
        if (series.SeriesEndDate is { } endDate && endDate < today)
            return EventSeriesMaterializationResult.Nothing(EventSeriesMaterializationOutcome.Ended, checkpoint);

        var target = Bound(series, today.AddDays(IEventSeriesMaterializer.RollingHorizonDays - 1));
        if (checkpoint >= target)
            return EventSeriesMaterializationResult.Nothing(EventSeriesMaterializationOutcome.UpToDate, checkpoint);

        return await MaterializeRangeAsync(series, rule, timeZone, today, ResumeDate(series, today), target, cancellationToken);
    }

    /// <summary>
    /// Where rolling materialization picks up: the day after the checkpoint, never before the
    /// series start and never before today. Past dates are never materialized (an occurrence
    /// that already happened cannot be joined), so a series with an unknown checkpoint, or one
    /// whose worker was down for a while, resumes at today; existing rows (by start instant and
    /// by occurrence index) and the unique index keep that recovery free of duplicates.
    /// </summary>
    private static DateOnly ResumeDate(EventSeries series, DateOnly today)
    {
        var resume = series.SeriesStartDate > today ? series.SeriesStartDate : today;
        if (series.MaterializedThroughLocalDate is { } checkpoint && checkpoint >= resume && checkpoint < DateOnly.MaxValue)
            resume = checkpoint.AddDays(1);
        return resume;
    }

    private static DateOnly Bound(EventSeries series, DateOnly throughLocalDate) =>
        series.SeriesEndDate is { } end && end < throughLocalDate ? end : throughLocalDate;

    private async Task<EventSeriesMaterializationResult> MaterializeRangeAsync(
        EventSeries series,
        RecurrenceRule rule,
        TimeZoneInfo timeZone,
        DateOnly today,
        DateOnly fromLocalDate,
        DateOnly throughLocalDate,
        CancellationToken cancellationToken)
    {
        var checkpointBefore = series.MaterializedThroughLocalDate;
        var through = Bound(series, throughLocalDate);

        var missing = await FindMissingAsync(series, rule, timeZone, fromLocalDate, through, cancellationToken);

        // The checkpoint only claims a contiguous evaluated horizon: it moves when this range
        // starts no later than where rolling materialization would resume.
        var advance = fromLocalDate <= ResumeDate(series, today) && (checkpointBefore is null || checkpointBefore < through);
        if (missing.Count == 0 && !advance)
            return EventSeriesMaterializationResult.Nothing(EventSeriesMaterializationOutcome.UpToDate, checkpointBefore);

        if (advance)
            series.MaterializedThroughLocalDate = through;

        // Always issue the RowVersion-checked series UPDATE, even when only occurrences are
        // inserted: if the series was cancelled (or changed by another materializer) after it was
        // read, the UPDATE matches no row and the whole transaction, inserts included, rolls back.
        _context.Entry(series).Property(s => s.MaterializedThroughLocalDate).IsModified = true;
        _context.Events.AddRange(missing);

        try
        {
            // One SaveChanges is one transaction: occurrences and checkpoint commit together.
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ResolveConflictAsync(series, missing, checkpointBefore, through, cancellationToken);
        }
        catch (DbUpdateException ex) when (EventSeriesOccurrenceConflictClassifier.IsDuplicateSeriesOccurrence(ex))
        {
            return await ResolveConflictAsync(series, missing, checkpointBefore, through, cancellationToken);
        }

        return new EventSeriesMaterializationResult(
            EventSeriesMaterializationOutcome.Materialized, missing.Count, checkpointBefore, series.MaterializedThroughLocalDate);
    }

    private async Task<List<EventOccurrence>> FindMissingAsync(
        EventSeries series,
        RecurrenceRule rule,
        TimeZoneInfo timeZone,
        DateOnly fromLocalDate,
        DateOnly throughLocalDate,
        CancellationToken cancellationToken)
    {
        var candidates = EventSeriesOccurrenceGenerator.Generate(series, rule, timeZone, fromLocalDate, throughLocalDate);
        if (candidates.Count == 0)
            return candidates;

        var seriesId = series.Id;
        var firstStart = candidates[0].ScheduledStartUtc;
        var lastStart = candidates[^1].ScheduledStartUtc;
        var firstIndex = candidates[0].OccurrenceIndex;
        var lastIndex = candidates[^1].OccurrenceIndex;

        var existing = await _context.Events
            .AsNoTracking()
            .Where(e => e.SeriesId == seriesId &&
                        ((e.ScheduledStartUtc >= firstStart && e.ScheduledStartUtc <= lastStart) ||
                         (e.OccurrenceIndex >= firstIndex && e.OccurrenceIndex <= lastIndex)))
            .Select(e => new { e.ScheduledStartUtc, e.OccurrenceIndex })
            .ToListAsync(cancellationToken);

        // Skipping by index too means an occurrence that was moved (detached) is not generated a
        // second time at its original slot.
        var existingStarts = existing.Select(e => e.ScheduledStartUtc).ToHashSet();
        var existingIndices = existing.Where(e => e.OccurrenceIndex.HasValue).Select(e => e.OccurrenceIndex!.Value).ToHashSet();

        return candidates
            .Where(o => !existingStarts.Contains(o.ScheduledStartUtc) && !existingIndices.Contains(o.OccurrenceIndex!.Value))
            .ToList();
    }

    /// <summary>
    /// The commit lost to a concurrent writer: either the series row changed (RowVersion) or
    /// another materializer inserted the same occurrence first (unique index). Nothing of ours was
    /// saved. Re-read the series once to report a deterministic outcome; no retry loop, the next
    /// pass resumes from whatever checkpoint is committed.
    /// </summary>
    private async Task<EventSeriesMaterializationResult> ResolveConflictAsync(
        EventSeries series,
        List<EventOccurrence> staged,
        DateOnly? checkpointBefore,
        DateOnly through,
        CancellationToken cancellationToken)
    {
        foreach (var occurrence in staged)
            _context.Entry(occurrence).State = EntityState.Detached;
        _context.Entry(series).State = EntityState.Detached;

        var current = await _context.EventSeries
            .AsNoTracking()
            .Where(s => s.Id == series.Id)
            .Select(s => new { s.Status, s.MaterializedThroughLocalDate })
            .FirstOrDefaultAsync(cancellationToken);

        if (current == null)
            return EventSeriesMaterializationResult.Nothing(EventSeriesMaterializationOutcome.NotFound);

        var outcome = current.Status != EventSeriesStatus.Active
            ? EventSeriesMaterializationOutcome.NotActive
            : current.MaterializedThroughLocalDate >= through
                ? EventSeriesMaterializationOutcome.UpToDate
                : EventSeriesMaterializationOutcome.Conflict;

        return new EventSeriesMaterializationResult(outcome, 0, checkpointBefore, current.MaterializedThroughLocalDate);
    }

    private static (RecurrenceRule Rule, TimeZoneInfo TimeZone) Resolve(EventSeries series)
    {
        var rule = RecurrenceRuleParser.Parse(series.RecurrenceRule);
        if (!IanaTimeZone.TryResolve(series.TimeZoneId, out var timeZone, out var timeZoneError))
            throw new InvalidOperationException($"Series {series.Id} has an unusable time zone: {timeZoneError}");
        return (rule, timeZone);
    }
}
