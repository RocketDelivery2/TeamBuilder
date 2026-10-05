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

    public EventSeriesMaterializer(TeamBuilderDbContext context)
    {
        _context = context;
    }

    public async Task<int> MaterializeAsync(Guid seriesId, DateOnly throughLocalDate, CancellationToken cancellationToken = default)
    {
        var series = await _context.EventSeries
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == seriesId, cancellationToken);
        if (series == null || series.Status != EventSeriesStatus.Active)
            return 0;

        var rule = RecurrenceRuleParser.Parse(series.RecurrenceRule);
        if (!IanaTimeZone.TryResolve(series.TimeZoneId, out var timeZone, out var timeZoneError))
            throw new InvalidOperationException($"Series {seriesId} has an unusable time zone: {timeZoneError}");

        var candidates = EventSeriesOccurrenceGenerator.Generate(series, rule, timeZone, throughLocalDate);
        if (candidates.Count == 0)
            return 0;

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

        var existingStarts = existing.Select(e => e.ScheduledStartUtc).ToHashSet();
        var existingIndices = existing.Where(e => e.OccurrenceIndex.HasValue).Select(e => e.OccurrenceIndex!.Value).ToHashSet();

        var missing = candidates
            .Where(o => !existingStarts.Contains(o.ScheduledStartUtc) && !existingIndices.Contains(o.OccurrenceIndex!.Value))
            .ToList();
        if (missing.Count == 0)
            return 0;

        _context.Events.AddRange(missing);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (EventSeriesOccurrenceConflictClassifier.IsDuplicateSeriesOccurrence(ex))
        {
            // A concurrent materialization inserted an occurrence of this series at the same
            // instant first. The batch is atomic, so none of ours was saved; the winner's rows
            // stand and any slot still missing is filled by the next invocation. No retry loop.
            foreach (var occurrence in missing)
                _context.Entry(occurrence).State = EntityState.Detached;
            return 0;
        }

        return missing.Count;
    }
}
