using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Infrastructure.Services;

public class EventService : IEventService
{
    private readonly TeamBuilderDbContext _context;

    public EventService(TeamBuilderDbContext context)
    {
        _context = context;
    }

    public async Task<EventDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var teamEvent = await _context.Events
            .Include(e => e.Team)
            .Include(e => e.Host)
            .Include(e => e.Venue)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

        return teamEvent == null ? null : MapToDto(teamEvent);
    }

    public async Task<PaginatedResult<EventDto>> GetAllAsync(
        int page, 
        int pageSize, 
        string? category = null, 
        string? region = null, 
        EventStatus? status = null, 
        CancellationToken cancellationToken = default)
    {
        var query = _context.Events
            .Include(e => e.Team)
            .Include(e => e.Host)
            .Include(e => e.Venue)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(e => e.Category == category);

        if (!string.IsNullOrWhiteSpace(region))
            query = query.Where(e => e.Region == region);

        if (status.HasValue)
            query = query.Where(e => e.Status == status.Value);

        var totalCount = await query.CountAsync(cancellationToken);

        var events = await query
            .OrderBy(e => e.ScheduledStartUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PaginatedResult<EventDto>
        {
            Items = events.Select(MapToDto),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<EventDto> CreateAsync(CreateEventDto createEventDto, Guid hostId, CancellationToken cancellationToken = default)
    {
        if (createEventDto.EventDateUtc is not { } eventDate)
            throw new ArgumentException("EventDateUtc is required.", nameof(createEventDto));

        var teamEvent = new EventOccurrence
        {
            Id = Guid.NewGuid(),
            Name = createEventDto.Name,
            Description = createEventDto.Description,
            ScheduledStartUtc = eventDate,
            Category = createEventDto.Category,
            Tags = createEventDto.Tags,
            LegacyLocation = createEventDto.Location,
            Region = createEventDto.Region,
            MaxParticipants = createEventDto.MaxParticipants,
            CurrentParticipantCount = 0,
            Status = EventStatus.Planned,
            TeamId = createEventDto.TeamId,
            HostId = hostId
        };

        _context.Events.Add(teamEvent);
        await _context.SaveChangesAsync(cancellationToken);

        return MapToDto(teamEvent);
    }

    public async Task<EventDto?> UpdateAsync(Guid id, UpdateEventDto updateEventDto, CancellationToken cancellationToken = default)
    {
        var teamEvent = await _context.Events
            .Include(e => e.Venue)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (teamEvent == null) return null;

        if (!string.IsNullOrWhiteSpace(updateEventDto.Name))
            teamEvent.Name = updateEventDto.Name;

        if (updateEventDto.Description != null)
            teamEvent.Description = updateEventDto.Description;

        if (updateEventDto.EventDateUtc.HasValue)
            teamEvent.ScheduledStartUtc = updateEventDto.EventDateUtc.Value;

        if (updateEventDto.Status.HasValue)
            teamEvent.Status = updateEventDto.Status.Value;

        if (updateEventDto.Category != null)
            teamEvent.Category = updateEventDto.Category;

        if (updateEventDto.Tags != null)
            teamEvent.Tags = updateEventDto.Tags;

        if (updateEventDto.Location != null)
            teamEvent.LegacyLocation = updateEventDto.Location;

        if (updateEventDto.Region != null)
            teamEvent.Region = updateEventDto.Region;

        if (updateEventDto.MaxParticipants.HasValue)
            teamEvent.MaxParticipants = updateEventDto.MaxParticipants.Value;

        // An explicit occurrence-level edit customizes a series-generated occurrence: it is now
        // independent of series-level behavior (e.g. series cancellation skips it). This holds
        // for any PUT, even one that changes no value. The series provenance (SeriesId,
        // OccurrenceIndex) is kept. One-off occurrences are never detached.
        if (teamEvent.SeriesId != null)
            teamEvent.IsDetached = true;

        await _context.SaveChangesAsync(cancellationToken);

        return MapToDto(teamEvent);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var teamEvent = await _context.Events.FindAsync([id], cancellationToken);
        if (teamEvent == null) return false;

        _context.Events.Remove(teamEvent);
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    internal static EventDto MapToDto(EventOccurrence teamEvent)
    {
        var scheduledStartUtc = AsUtc(teamEvent.ScheduledStartUtc);
        return new EventDto
        {
            Id = teamEvent.Id,
            Name = teamEvent.Name,
            Description = teamEvent.Description,
            EventDateUtc = scheduledStartUtc,
            ScheduledStartUtc = scheduledStartUtc,
            ScheduledEndUtc = teamEvent.ScheduledEndUtc is { } end ? AsUtc(end) : null,
            Status = teamEvent.Status,
            Category = teamEvent.Category,
            Tags = teamEvent.Tags,
            Location = teamEvent.DisplayLocation,
            Region = teamEvent.Region,
            MaxParticipants = teamEvent.MaxParticipants,
            CurrentParticipantCount = teamEvent.CurrentParticipantCount,
            SeriesId = teamEvent.SeriesId,
            VenueId = teamEvent.VenueId,
            IsDetached = teamEvent.IsDetached,
            TeamId = teamEvent.TeamId,
            TeamName = teamEvent.Team?.Name,
            HostId = teamEvent.HostId,
            HostUsername = teamEvent.Host?.Username,
            CreatedAtUtc = teamEvent.CreatedAtUtc,
            UpdatedAtUtc = teamEvent.UpdatedAtUtc
        };
    }

    /// <summary>
    /// Scheduled instants are stored as UTC, but SQL Server datetime2 does not keep
    /// <see cref="DateTime.Kind"/>, so values read back are Unspecified. Marking them UTC makes
    /// the API serialize them with a <c>Z</c> suffix.
    /// </summary>
    internal static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
