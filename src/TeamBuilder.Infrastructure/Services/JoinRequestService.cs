using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Persistence;

namespace TeamBuilder.Infrastructure.Services;

public class JoinRequestService : IJoinRequestService
{
    private readonly TeamBuilderDbContext _context;

    public JoinRequestService(TeamBuilderDbContext context)
    {
        _context = context;
    }

    public async Task<JoinRequestDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var joinRequest = await _context.JoinRequests
            .Include(jr => jr.Team)
            .Include(jr => jr.Player)
            .FirstOrDefaultAsync(jr => jr.Id == id, cancellationToken);

        return joinRequest == null ? null : MapToDto(joinRequest);
    }

    public async Task<PaginatedResult<JoinRequestDto>> GetByTeamIdAsync(
        Guid teamId, 
        int page, 
        int pageSize, 
        RequestStatus? status = null, 
        CancellationToken cancellationToken = default)
    {
        var query = _context.JoinRequests
            .Include(jr => jr.Team)
            .Include(jr => jr.Player)
            .Where(jr => jr.TeamId == teamId);

        if (status.HasValue)
            query = query.Where(jr => jr.Status == status.Value);

        var totalCount = await query.CountAsync(cancellationToken);

        var joinRequests = await query
            .OrderByDescending(jr => jr.RequestedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PaginatedResult<JoinRequestDto>
        {
            Items = joinRequests.Select(MapToDto),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<PaginatedResult<JoinRequestDto>> GetByPlayerIdAsync(
        Guid playerId, 
        int page, 
        int pageSize, 
        RequestStatus? status = null, 
        CancellationToken cancellationToken = default)
    {
        var query = _context.JoinRequests
            .Include(jr => jr.Team)
            .Include(jr => jr.Player)
            .Where(jr => jr.PlayerId == playerId);

        if (status.HasValue)
            query = query.Where(jr => jr.Status == status.Value);

        var totalCount = await query.CountAsync(cancellationToken);

        var joinRequests = await query
            .OrderByDescending(jr => jr.RequestedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PaginatedResult<JoinRequestDto>
        {
            Items = joinRequests.Select(MapToDto),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<JoinRequestDto> CreateAsync(CreateJoinRequestDto createJoinRequestDto, Guid playerId, CancellationToken cancellationToken = default)
    {
        if (createJoinRequestDto.TeamId is not { } teamId || teamId == Guid.Empty)
            throw new ArgumentException("TeamId is required and must not be an empty GUID.", nameof(createJoinRequestDto));

        var existingRequest = await _context.JoinRequests
            .FirstOrDefaultAsync(jr => jr.TeamId == teamId &&
                                      jr.PlayerId == playerId &&
                                      jr.Status == RequestStatus.Pending, cancellationToken);

        if (existingRequest != null)
            throw new InvalidOperationException("A pending join request already exists for this team.");

        var joinRequest = new JoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            PlayerId = playerId,
            Message = createJoinRequestDto.Message,
            Status = RequestStatus.Pending,
            RequestedAtUtc = DateTime.UtcNow
        };

        _context.JoinRequests.Add(joinRequest);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (JoinRequestConflictClassifier.IsDuplicatePendingJoinRequest(ex))
        {
            throw new InvalidOperationException(
                "A pending join request already exists for this team.",
                ex);
        }

        return MapToDto(joinRequest);
    }

    public async Task<JoinRequestDto?> ProcessAsync(
        Guid id, 
        ProcessJoinRequestDto processJoinRequestDto, 
        Guid processedByUserId, 
        CancellationToken cancellationToken = default)
    {
        var joinRequest = await _context.JoinRequests
            .Include(jr => jr.Team)
            .Include(jr => jr.Player)
            .FirstOrDefaultAsync(jr => jr.Id == id, cancellationToken);

        if (joinRequest == null) return null;

        if (joinRequest.Status != RequestStatus.Pending)
            throw new InvalidOperationException("Only pending requests can be processed.");

        if (joinRequest.Team is null)
            throw new InvalidOperationException("The team for this join request could not be loaded.");

        if (processJoinRequestDto.Status == RequestStatus.Approved &&
            joinRequest.Team.CurrentMemberCount >= joinRequest.Team.MaxMembers)
            throw new InvalidOperationException("The team is already full.");

        joinRequest.Status = processJoinRequestDto.Status;
        joinRequest.ProcessedAtUtc = DateTime.UtcNow;
        joinRequest.ProcessedByUserId = processedByUserId;

        if (processJoinRequestDto.Status == RequestStatus.Approved)
        {
            // Friendly early validation: catches the common case without a SaveChanges
            // failure (a raw unique-index violation). The unique index is still the source
            // of truth for concurrent races.
            var alreadyActiveMember = await _context.TeamMembers.AnyAsync(
                tm => tm.TeamId == joinRequest.TeamId && tm.PlayerId == joinRequest.PlayerId && tm.IsActive,
                cancellationToken);

            if (alreadyActiveMember)
                throw new InvalidOperationException("This player is already an active member of this team.");

            var teamMember = new TeamMember
            {
                Id = Guid.NewGuid(),
                TeamId = joinRequest.TeamId,
                PlayerId = joinRequest.PlayerId,
                Role = TeamRole.Member,
                JoinedAtUtc = DateTime.UtcNow,
                IsActive = true
            };

            _context.TeamMembers.Add(teamMember);

            joinRequest.Team.CurrentMemberCount++;
            if (joinRequest.Team.CurrentMemberCount >= joinRequest.Team.MaxMembers)
            {
                joinRequest.Team.Status = TeamStatus.Full;
            }
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new InvalidOperationException(
                "The team changed while this join request was being processed. Please try again.",
                ex);
        }
        catch (DbUpdateException ex) when (TeamMembershipConflictClassifier.IsDuplicateTeamMembership(ex))
        {
            throw new InvalidOperationException(
                "This player is already an active member of this team.",
                ex);
        }

        return MapToDto(joinRequest);
    }

    private static JoinRequestDto MapToDto(JoinRequest joinRequest)
    {
        return new JoinRequestDto
        {
            Id = joinRequest.Id,
            TeamId = joinRequest.TeamId,
            TeamName = joinRequest.Team?.Name,
            PlayerId = joinRequest.PlayerId,
            PlayerUsername = joinRequest.Player?.Username,
            Status = joinRequest.Status,
            Message = joinRequest.Message,
            RequestedAtUtc = joinRequest.RequestedAtUtc,
            ProcessedAtUtc = joinRequest.ProcessedAtUtc
        };
    }
}
