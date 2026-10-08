using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Persistence;

namespace TeamBuilder.Infrastructure.Services;

public class EventService : IEventService
{
    public const string OccurrenceChangedMessage =
        "The event changed while it was being saved. Reload it and try again.";
    public const string ParticipationHistoryMessage =
        "This event has roster participation history and cannot be deleted. Cancel it instead.";
    public const string ClosedOccurrenceTransferMessage =
        "Hosting cannot be transferred for a completed, cancelled or archived event.";
    public const string HostTransferTargetNotLinkedMessage =
        "The new host has no linked sign-in identity, so they could not manage this event.";

    private readonly TeamBuilderDbContext _context;
    private readonly TimeProvider _timeProvider;

    public EventService(TeamBuilderDbContext context, TimeProvider? timeProvider = null)
    {
        _context = context;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

    /// <summary>
    /// Creates a standalone or team occurrence hosted by <paramref name="hostId"/>, optionally
    /// with its initial roster requirements and the host's own participation, in one atomic
    /// commit: the occurrence, every requirement and the host's assignment are saved by a single
    /// SaveChanges (one database transaction), so any failure leaves nothing behind. Hosting is
    /// administrative only; the host consumes capacity only when <c>HostParticipates</c> is true,
    /// through the same allocation invariant as every other assignment.
    /// </summary>
    public async Task<EventDto> CreateAsync(CreateEventDto createEventDto, Guid hostId, CancellationToken cancellationToken = default)
    {
        if (createEventDto.EventDateUtc is not { } eventDate)
            throw new ArgumentException("EventDateUtc is required.", nameof(createEventDto));

        var plannedRequirements = PlanRequirements(createEventDto);

        // 404 for an unknown venue, 403 for someone else's Private venue (hosting it would
        // reveal its address). Nothing is written before these checks pass.
        var venue = createEventDto.VenueId is { } venueId
            ? await VenueService.EnsureAttachableAsync(_context, venueId, hostId, cancellationToken)
            : null;

        var teamEvent = new EventOccurrence
        {
            Id = Guid.NewGuid(),
            Name = createEventDto.Name,
            Description = createEventDto.Description,
            ScheduledStartUtc = eventDate,
            ScheduledEndUtc = createEventDto.ScheduledEndUtc,
            Category = createEventDto.Category,
            Tags = createEventDto.Tags,
            LegacyLocation = createEventDto.Location,
            Region = createEventDto.Region,
            MaxParticipants = createEventDto.MaxParticipants,
            CurrentParticipantCount = 0,
            Status = EventStatus.Planned,
            TeamId = createEventDto.TeamId,
            VenueId = createEventDto.VenueId,
            HostId = hostId
        };

        _context.Events.Add(teamEvent);

        var requirements = plannedRequirements
            .Select(r => EventRosterService.NewRequirement(teamEvent.Id, r.RoleCode, r.RequiredCount, r.DisplayPosition, r.SourceRoleLabel))
            .ToList();
        _context.RosterRequirements.AddRange(requirements);

        if (createEventDto.HostParticipates == true)
        {
            var hostRequirement = requirements[HostRequirementIndex(createEventDto, plannedRequirements)];
            await new EventRosterService(_context, _timeProvider).StageAllocationAsync(
                teamEvent.Id,
                hostId,
                hostRequirement,
                hostRequirement.RoleCode,
                sourceRoleLabel: null,
                RosterAssignmentStatus.Confirmed,
                RosterAssignmentSource.Player,
                replacedAssignmentId: null,
                cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);

        var dto = MapToDto(teamEvent);
        if (venue is not null)
            dto.Location = venue.Name;
        return dto;
    }

    private sealed record PlannedRequirement(string RoleCode, int RequiredCount, string? DisplayPosition, string? SourceRoleLabel);

    /// <summary>
    /// Validates the optional initial roster with the same role-code normalization and count
    /// rules as <c>POST …/roster/requirements</c>. Role codes must be distinct after
    /// normalization; nothing is written before the whole request is valid.
    /// </summary>
    private static List<PlannedRequirement> PlanRequirements(CreateEventDto createEventDto)
    {
        var planned = new List<PlannedRequirement>();
        foreach (var requirement in createEventDto.RosterRequirements ?? [])
        {
            if (requirement is null)
                throw new ArgumentException("RosterRequirements must not contain null entries.");
            if (requirement.RequiredCount is not { } requiredCount)
                throw new ArgumentException("RequiredCount is required for every roster requirement.");

            var roleCode = EventRosterService.NormalizeRoleCode(requirement.RoleCode) ?? RosterRoleCodes.Participant;
            if (planned.Any(p => p.RoleCode == roleCode))
            {
                throw new RosterValidationException(
                    RosterValidationCodes.DuplicateRequirementRoleInRequest,
                    $"Role code '{roleCode}' appears more than once in rosterRequirements.");
            }

            planned.Add(new PlannedRequirement(roleCode, requiredCount, requirement.DisplayPosition, requirement.SourceRoleLabel));
        }

        return planned;
    }

    /// <summary>
    /// The requirement the host fills when <c>HostParticipates</c> is true: the only one, or
    /// the one named by <c>HostRoleCode</c> (required when there are several).
    /// </summary>
    private static int HostRequirementIndex(CreateEventDto createEventDto, List<PlannedRequirement> planned)
    {
        if (planned.Count == 0)
        {
            throw new RosterValidationException(
                RosterValidationCodes.HostParticipationRequiresRequirement,
                "hostParticipates requires at least one roster requirement for the host to fill.");
        }

        if (createEventDto.HostRoleCode is null)
        {
            if (planned.Count == 1)
                return 0;

            throw new RosterValidationException(
                RosterValidationCodes.HostRoleCodeInvalid,
                "hostRoleCode is required when hostParticipates is true and there are several roster requirements.");
        }

        var hostRoleCode = EventRosterService.NormalizeRoleCode(createEventDto.HostRoleCode);
        var index = planned.FindIndex(p => p.RoleCode == hostRoleCode);
        if (index < 0)
        {
            throw new RosterValidationException(
                RosterValidationCodes.HostRoleCodeInvalid,
                "hostRoleCode must name one of the roster requirements in this request.");
        }

        return index;
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

        await SaveOccurrenceChangesAsync(cancellationToken);

        return MapToDto(teamEvent);
    }

    /// <summary>
    /// Hard-deletes an occurrence that has no roster participation history. One that has any
    /// RosterAssignment row (live or historical) is refused with 409
    /// OccurrenceHasParticipationHistory: cancel it instead. The NO ACTION foreign key from
    /// RosterAssignments makes the same refusal race-safe. Requirements and imported
    /// RosterEntries still go with the occurrence.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var teamEvent = await _context.Events.FindAsync([id], cancellationToken);
        if (teamEvent == null) return false;

        if (await _context.RosterAssignments.AnyAsync(a => a.OccurrenceId == id, cancellationToken))
            throw new RosterConflictException(RosterConflictCodes.OccurrenceHasParticipationHistory, ParticipationHistoryMessage);

        _context.Events.Remove(teamEvent);
        try
        {
            await SaveOccurrenceChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (RosterConflictClassifier.IsOccurrenceWithParticipationHistoryDelete(ex))
        {
            throw new RosterConflictException(RosterConflictCodes.OccurrenceHasParticipationHistory, ParticipationHistoryMessage, ex);
        }

        return true;
    }

    public async Task<EventDto> TransferHostAsync(
        Guid occurrenceId,
        Guid callerPlayerId,
        Guid newHostPlayerId,
        CancellationToken cancellationToken = default)
    {
        var occurrence = await _context.Events
            .Include(e => e.Team)
            .Include(e => e.Venue)
            .FirstOrDefaultAsync(e => e.Id == occurrenceId, cancellationToken)
            ?? throw new EventOccurrenceNotFoundException(occurrenceId);

        if (occurrence.HostId is null)
            throw new RosterConflictException(RosterConflictCodes.OccurrenceHasNoHost, EventRosterService.NoHostMessage);

        if (occurrence.HostId != callerPlayerId)
            throw new OccurrenceHostForbiddenException(occurrenceId, callerPlayerId);

        // Stewardship of a finished occurrence is history: it is never rewritten. A status change
        // racing this transfer is caught by the RowVersion check below (OccurrenceChanged).
        if (!RosterState.AcceptsNewRosterMutations(occurrence.Status))
            throw new RosterConflictException(RosterConflictCodes.OccurrenceClosed, ClosedOccurrenceTransferMessage);

        if (!await _context.Players.AnyAsync(p => p.Id == newHostPlayerId, cancellationToken))
            throw new ArgumentException("NewHostPlayerId does not reference an existing player.");

        // The event must never be handed to an account that cannot sign in to manage it.
        if (!await _context.PlayerIdentities.AnyAsync(pi => pi.PlayerId == newHostPlayerId, cancellationToken))
            throw new RosterConflictException(RosterConflictCodes.HostTransferTargetNotLinked, HostTransferTargetNotLinkedMessage);

        if (newHostPlayerId != callerPlayerId)
        {
            // Only the host changes: roster assignments, requirements, capacity, TeamId and
            // status are untouched, and the target may or may not hold a roster spot. A series
            // occurrence keeps its series provenance but now carries an occurrence-specific host,
            // so it is detached like any other occurrence-level edit; the series' own host is
            // unchanged.
            occurrence.HostId = newHostPlayerId;
            if (occurrence.SeriesId != null)
                occurrence.IsDetached = true;

            // RowVersion-checked: of concurrent transfers exactly one commits; the others get
            // 409 OccurrenceChanged and, on reload, are no longer the host.
            await SaveOccurrenceChangesAsync(cancellationToken);
        }

        var dto = MapToDto(occurrence);
        dto.HostUsername = await _context.Players
            .Where(p => p.Id == newHostPlayerId)
            .Select(p => p.Username)
            .FirstOrDefaultAsync(cancellationToken);
        return dto;
    }

    /// <summary>
    /// Saves a change to a tracked occurrence. Its RowVersion makes a concurrent occurrence
    /// change (host transfer, host roster action, status edit) a clean 409 OccurrenceChanged
    /// instead of a silent last write wins or a 500.
    /// </summary>
    private async Task SaveOccurrenceChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new RosterConflictException(RosterConflictCodes.OccurrenceChanged, OccurrenceChangedMessage, ex);
        }
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
