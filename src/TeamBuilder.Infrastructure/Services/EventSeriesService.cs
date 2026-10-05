using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;
using TeamBuilder.Application.Scheduling;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Infrastructure.Services;

public class EventSeriesService : IEventSeriesService
{
    private readonly TeamBuilderDbContext _context;
    private readonly TimeProvider _timeProvider;

    public EventSeriesService(TeamBuilderDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<EventSeriesDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var series = await _context.EventSeries
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        return series == null ? null : MapToDto(series);
    }

    public async Task<PaginatedResult<EventDto>?> GetOccurrencesAsync(Guid id, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        if (!await _context.EventSeries.AnyAsync(s => s.Id == id, cancellationToken))
            return null;

        var query = _context.Events
            .AsNoTracking()
            .Where(e => e.SeriesId == id);

        var totalCount = await query.CountAsync(cancellationToken);

        var occurrences = await query
            .Include(e => e.Team)
            .Include(e => e.Host)
            .Include(e => e.Venue)
            .OrderBy(e => e.ScheduledStartUtc)
            .ThenBy(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PaginatedResult<EventDto>
        {
            Items = occurrences.Select(EventService.MapToDto),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<EventSeriesDto> CreateAsync(CreateEventSeriesDto createDto, Guid hostId, CancellationToken cancellationToken = default)
    {
        if (createDto.LocalStartTime is not { } localStartTime ||
            createDto.DurationMinutes is not { } durationMinutes ||
            createDto.SeriesStartDate is not { } seriesStartDate ||
            createDto.MaxParticipants is not { } maxParticipants)
        {
            throw new ArgumentException("LocalStartTime, DurationMinutes, SeriesStartDate and MaxParticipants are required.");
        }

        var rule = RecurrenceRuleParser.Parse(createDto.RecurrenceRule);

        if (createDto.SeriesEndDate is { } endDate && endDate < seriesStartDate)
            throw new ArgumentException("SeriesEndDate must be on or after SeriesStartDate.");

        string? venueTimeZoneId = null;
        if (createDto.VenueId is { } venueId)
        {
            var venue = await _context.Venues
                .AsNoTracking()
                .Where(v => v.Id == venueId)
                .Select(v => new { v.TimeZoneId })
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new VenueNotFoundException(venueId);

            // A venue time zone that does not resolve is treated as absent ("no usable venue
            // time zone"), so the request must then supply one.
            if (venue.TimeZoneId is not null && IanaTimeZone.TryResolve(venue.TimeZoneId, out _, out _))
                venueTimeZoneId = venue.TimeZoneId;
        }

        string timeZoneId;
        if (venueTimeZoneId is not null)
        {
            if (createDto.TimeZoneId is not null && !string.Equals(createDto.TimeZoneId, venueTimeZoneId, StringComparison.Ordinal))
                throw new ArgumentException($"TimeZoneId must match the venue's time zone '{venueTimeZoneId}' or be omitted.");
            timeZoneId = venueTimeZoneId;
        }
        else
        {
            timeZoneId = createDto.TimeZoneId
                ?? throw new ArgumentException("TimeZoneId is required when the series has no venue with a time zone.");
        }

        if (!IanaTimeZone.TryResolve(timeZoneId, out var timeZone, out var timeZoneError))
            throw new ArgumentException(timeZoneError);

        var today = SeriesLocalTime.Today(_timeProvider, timeZone);
        if (seriesStartDate < today)
            throw new ArgumentException($"SeriesStartDate must not be before the current date in {timeZoneId} ({today:yyyy-MM-dd}).");

        var series = new EventSeries
        {
            Id = Guid.NewGuid(),
            Name = createDto.Name,
            Description = createDto.Description,
            Category = createDto.Category,
            Tags = createDto.Tags,
            TeamId = createDto.TeamId,
            HostId = hostId,
            VenueId = createDto.VenueId,
            LocalStartTime = localStartTime,
            DurationMinutes = durationMinutes,
            TimeZoneId = timeZoneId,
            RecurrenceRule = createDto.RecurrenceRule,
            SeriesStartDate = seriesStartDate,
            SeriesEndDate = createDto.SeriesEndDate,
            MaxParticipants = maxParticipants,
            Status = EventSeriesStatus.Active
        };

        // Initial rolling window, anchored on the series start date (a new series may start in
        // the future): local dates start .. start + 20 days, bounded by the end date.
        var throughLocalDate = seriesStartDate.AddDays(IEventSeriesMaterializer.InitialHorizonDays - 1);
        var occurrences = EventSeriesOccurrenceGenerator.Generate(series, rule, timeZone, throughLocalDate);

        // One SaveChanges is one database transaction: the series and its initial occurrences
        // are committed together or not at all.
        _context.EventSeries.Add(series);
        _context.Events.AddRange(occurrences);
        await _context.SaveChangesAsync(cancellationToken);

        return MapToDto(series);
    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var series = await _context.EventSeries.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (series == null)
            return false;

        if (series.Status == EventSeriesStatus.Cancelled)
            throw new InvalidOperationException("This event series is already cancelled.");

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var futureOccurrences = await _context.Events
            .Where(e => e.SeriesId == id &&
                        !e.IsDetached &&
                        e.ScheduledStartUtc > nowUtc &&
                        e.Status != EventStatus.Cancelled)
            .ToListAsync(cancellationToken);

        series.Status = EventSeriesStatus.Cancelled;
        foreach (var occurrence in futureOccurrences)
            occurrence.Status = EventStatus.Cancelled;

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new InvalidOperationException(
                "The event series changed while it was being cancelled. Please reload it and try again.",
                ex);
        }

        return true;
    }

    private static EventSeriesDto MapToDto(EventSeries series)
    {
        return new EventSeriesDto
        {
            Id = series.Id,
            Name = series.Name,
            Description = series.Description,
            Category = series.Category,
            Tags = series.Tags,
            TeamId = series.TeamId,
            HostId = series.HostId,
            VenueId = series.VenueId,
            LocalStartTime = series.LocalStartTime,
            DurationMinutes = series.DurationMinutes,
            TimeZoneId = series.TimeZoneId,
            RecurrenceRule = series.RecurrenceRule,
            SeriesStartDate = series.SeriesStartDate,
            SeriesEndDate = series.SeriesEndDate,
            Status = series.Status,
            MaxParticipants = series.MaxParticipants,
            CreatedAtUtc = EventService.AsUtc(series.CreatedAtUtc),
            UpdatedAtUtc = series.UpdatedAtUtc is { } updated ? EventService.AsUtc(updated) : null
        };
    }
}
