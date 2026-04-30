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
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(e => e.Category == category);

        if (!string.IsNullOrWhiteSpace(region))
            query = query.Where(e => e.Region == region);

        if (status.HasValue)
            query = query.Where(e => e.Status == status.Value);

        var totalCount = await query.CountAsync(cancellationToken);

        var events = await query
            .OrderBy(e => e.EventDateUtc)
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
        var teamEvent = new TeamEvent
        {
            Id = Guid.NewGuid(),
            Name = createEventDto.Name,
            Description = createEventDto.Description,
            EventDateUtc = createEventDto.EventDateUtc,
            Category = createEventDto.Category,
            Tags = createEventDto.Tags,
            Location = createEventDto.Location,
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
        var teamEvent = await _context.Events.FindAsync([id], cancellationToken);
        if (teamEvent == null) return null;

        if (!string.IsNullOrWhiteSpace(updateEventDto.Name))
            teamEvent.Name = updateEventDto.Name;

        if (updateEventDto.Description != null)
            teamEvent.Description = updateEventDto.Description;

        if (updateEventDto.EventDateUtc.HasValue)
            teamEvent.EventDateUtc = updateEventDto.EventDateUtc.Value;

        if (updateEventDto.Status.HasValue)
            teamEvent.Status = updateEventDto.Status.Value;

        if (updateEventDto.Category != null)
            teamEvent.Category = updateEventDto.Category;

        if (updateEventDto.Tags != null)
            teamEvent.Tags = updateEventDto.Tags;

        if (updateEventDto.Location != null)
            teamEvent.Location = updateEventDto.Location;

        if (updateEventDto.Region != null)
            teamEvent.Region = updateEventDto.Region;

        if (updateEventDto.MaxParticipants.HasValue)
            teamEvent.MaxParticipants = updateEventDto.MaxParticipants.Value;

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

    private static EventDto MapToDto(TeamEvent teamEvent)
    {
        return new EventDto
        {
            Id = teamEvent.Id,
            Name = teamEvent.Name,
            Description = teamEvent.Description,
            EventDateUtc = teamEvent.EventDateUtc,
            Status = teamEvent.Status,
            Category = teamEvent.Category,
            Tags = teamEvent.Tags,
            Location = teamEvent.Location,
            Region = teamEvent.Region,
            MaxParticipants = teamEvent.MaxParticipants,
            CurrentParticipantCount = teamEvent.CurrentParticipantCount,
            TeamId = teamEvent.TeamId,
            TeamName = teamEvent.Team?.Name,
            HostId = teamEvent.HostId,
            HostUsername = teamEvent.Host?.Username,
            CreatedAtUtc = teamEvent.CreatedAtUtc,
            UpdatedAtUtc = teamEvent.UpdatedAtUtc
        };
    }
}
