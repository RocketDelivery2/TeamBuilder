using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Infrastructure.Services;

public class TeamService : ITeamService
{
    private readonly TeamBuilderDbContext _context;

    public TeamService(TeamBuilderDbContext context)
    {
        _context = context;
    }

    public async Task<TeamDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var team = await _context.Teams
            .Include(t => t.Owner)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        return team == null ? null : MapToDto(team);
    }

    public async Task<PaginatedResult<TeamDto>> GetAllAsync(
        int page, 
        int pageSize, 
        string? category = null, 
        string? region = null, 
        TeamStatus? status = null, 
        CancellationToken cancellationToken = default)
    {
        var query = _context.Teams.Include(t => t.Owner).AsQueryable();

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(t => t.Category == category);

        if (!string.IsNullOrWhiteSpace(region))
            query = query.Where(t => t.Region == region);

        if (status.HasValue)
            query = query.Where(t => t.Status == status.Value);

        var totalCount = await query.CountAsync(cancellationToken);

        var teams = await query
            .OrderByDescending(t => t.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PaginatedResult<TeamDto>
        {
            Items = teams.Select(MapToDto),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<TeamDto> CreateAsync(CreateTeamDto createTeamDto, Guid ownerId, CancellationToken cancellationToken = default)
    {
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = createTeamDto.Name,
            Description = createTeamDto.Description,
            MaxMembers = createTeamDto.MaxMembers,
            CurrentMemberCount = 0,
            Region = createTeamDto.Region,
            Category = createTeamDto.Category,
            Tags = createTeamDto.Tags,
            Status = TeamStatus.Recruiting,
            OwnerId = ownerId
        };

        _context.Teams.Add(team);
        await _context.SaveChangesAsync(cancellationToken);

        return MapToDto(team);
    }

    public async Task<TeamDto?> UpdateAsync(Guid id, UpdateTeamDto updateTeamDto, CancellationToken cancellationToken = default)
    {
        var team = await _context.Teams.FindAsync([id], cancellationToken);
        if (team == null) return null;

        if (!string.IsNullOrWhiteSpace(updateTeamDto.Name))
            team.Name = updateTeamDto.Name;

        if (updateTeamDto.Description != null)
            team.Description = updateTeamDto.Description;

        if (updateTeamDto.Status.HasValue)
            team.Status = updateTeamDto.Status.Value;

        if (updateTeamDto.MaxMembers.HasValue)
            team.MaxMembers = updateTeamDto.MaxMembers.Value;

        if (updateTeamDto.Region != null)
            team.Region = updateTeamDto.Region;

        if (updateTeamDto.Category != null)
            team.Category = updateTeamDto.Category;

        if (updateTeamDto.Tags != null)
            team.Tags = updateTeamDto.Tags;

        await _context.SaveChangesAsync(cancellationToken);

        return MapToDto(team);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var team = await _context.Teams.FindAsync([id], cancellationToken);
        if (team == null) return false;

        _context.Teams.Remove(team);
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> RemoveMemberAsync(Guid teamId, Guid playerId, CancellationToken cancellationToken = default)
    {
        var teamMember = await _context.TeamMembers
            .FirstOrDefaultAsync(tm => tm.TeamId == teamId && tm.PlayerId == playerId && tm.IsActive, cancellationToken);

        if (teamMember == null) return false;

        // Mark as inactive instead of deleting
        teamMember.IsActive = false;

        // Decrement current member count
        var team = await _context.Teams.FindAsync([teamId], cancellationToken);
        if (team != null && team.CurrentMemberCount > 0)
        {
            team.CurrentMemberCount--;

            // If team was full and now has space, change status to Recruiting
            if (team.Status == TeamStatus.Full && team.CurrentMemberCount < team.MaxMembers)
            {
                team.Status = TeamStatus.Recruiting;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    private static TeamDto MapToDto(Team team)
    {
        return new TeamDto
        {
            Id = team.Id,
            Name = team.Name,
            Description = team.Description,
            Status = team.Status,
            MaxMembers = team.MaxMembers,
            CurrentMemberCount = team.CurrentMemberCount,
            Region = team.Region,
            Category = team.Category,
            Tags = team.Tags,
            OwnerId = team.OwnerId,
            OwnerUsername = team.Owner?.Username,
            CreatedAtUtc = team.CreatedAtUtc,
            UpdatedAtUtc = team.UpdatedAtUtc
        };
    }
}
