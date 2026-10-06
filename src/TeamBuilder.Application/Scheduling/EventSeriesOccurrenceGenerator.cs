using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Application.Scheduling;

/// <summary>
/// Builds the concrete occurrences of a series up to a local date. Pure and deterministic: the
/// caller decides which already exist and persists the rest, so series creation and later
/// rolling materialization use exactly the same generation rules.
/// </summary>
public static class EventSeriesOccurrenceGenerator
{
    /// <summary>
    /// Occurrences of <paramref name="series"/> whose local date is on or before
    /// <paramref name="throughLocalDate"/>, skipping any whose start instant or occurrence index
    /// is already taken. Skipping by index too means an occurrence that was later moved
    /// (detached) is not generated a second time at its original slot.
    /// </summary>
    public static List<EventOccurrence> Generate(
        EventSeries series,
        RecurrenceRule rule,
        TimeZoneInfo timeZone,
        DateOnly throughLocalDate,
        IReadOnlySet<DateTime>? existingStartsUtc = null,
        IReadOnlySet<int>? existingIndices = null) =>
        Generate(series, rule, timeZone, series.SeriesStartDate, throughLocalDate, existingStartsUtc, existingIndices);

    /// <summary>
    /// Occurrences of <paramref name="series"/> whose local date is within
    /// <paramref name="fromLocalDate"/>..<paramref name="throughLocalDate"/> (inclusive, bounded by
    /// the series dates). Each keeps the occurrence index full generation would assign.
    /// </summary>
    public static List<EventOccurrence> Generate(
        EventSeries series,
        RecurrenceRule rule,
        TimeZoneInfo timeZone,
        DateOnly fromLocalDate,
        DateOnly throughLocalDate,
        IReadOnlySet<DateTime>? existingStartsUtc = null,
        IReadOnlySet<int>? existingIndices = null)
    {
        var occurrences = new List<EventOccurrence>();

        foreach (var (index, date) in RecurrenceSchedule.Enumerate(rule, series.SeriesStartDate, series.SeriesEndDate, fromLocalDate, throughLocalDate))
        {
            var startUtc = SeriesLocalTime.ToUtc(date, series.LocalStartTime, timeZone);
            if (existingStartsUtc?.Contains(startUtc) == true || existingIndices?.Contains(index) == true)
                continue;

            occurrences.Add(new EventOccurrence
            {
                Id = Guid.NewGuid(),
                SeriesId = series.Id,
                TeamId = series.TeamId,
                HostId = series.HostId,
                VenueId = series.VenueId,
                Name = series.Name,
                Description = series.Description,
                Category = series.Category,
                Tags = series.Tags,
                MaxParticipants = series.MaxParticipants,
                CurrentParticipantCount = 0,
                Status = EventStatus.Planned,
                IsDetached = false,
                LegacyLocation = null,
                OccurrenceIndex = index,
                ScheduledStartUtc = startUtc,
                ScheduledEndUtc = startUtc.AddMinutes(series.DurationMinutes)
            });
        }

        return occurrences;
    }
}
