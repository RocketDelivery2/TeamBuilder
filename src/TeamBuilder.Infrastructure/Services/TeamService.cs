using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;
using TeamBuilder.Application.Validation;
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
        TeamLifecycleStatus? lifecycleStatus = null,
        bool? hasVacancies = null,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Teams.Include(t => t.Owner).AsQueryable();

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(t => t.Category == category);

        if (!string.IsNullOrWhiteSpace(region))
            query = query.Where(t => t.Region == region);

        // All filters combine with AND semantics, and are applied before the count so the
        // total reflects them. The legacy status is translated to the stored facts it is
        // derived from (see TeamState.ToLegacyStatus).
        if (status.HasValue)
            query = ApplyLegacyStatusFilter(query, status.Value);

        if (lifecycleStatus.HasValue)
            query = query.Where(t => t.LifecycleStatus == lifecycleStatus.Value);

        if (hasVacancies == true)
            query = query.Where(t =>
                t.LifecycleStatus == TeamLifecycleStatus.Active
                && t.IsAcceptingMembers
                && t.CurrentMemberCount < t.MaxMembers);
        else if (hasVacancies == false)
            query = query.Where(t =>
                !(t.LifecycleStatus == TeamLifecycleStatus.Active
                  && t.IsAcceptingMembers
                  && t.CurrentMemberCount < t.MaxMembers));

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
            LifecycleStatus = TeamLifecycleStatus.Active,
            IsAcceptingMembers = true,
            OwnerId = ownerId
        };

        _context.Teams.Add(team);
        await _context.SaveChangesAsync(cancellationToken);

        return MapToDto(team);
    }

    public async Task<TeamDto?> UpdateAsync(Guid id, UpdateTeamDto updateTeamDto, CancellationToken cancellationToken = default)
    {
        // Load the team (and its RowVersion) before counting memberships so any membership
        // change committed after this point also bumps the RowVersion and fails our save.
        var team = await _context.Teams.FindAsync([id], cancellationToken);
        if (team == null) return null;

        // Validate the lifecycle/recruitment change before touching anything (400 on invalid).
        var (lifecycleStatus, isAcceptingMembers) = TeamStateUpdateResolver.Resolve(
            team.LifecycleStatus,
            team.IsAcceptingMembers,
            updateTeamDto);

        // Active TeamMember rows are the roster occupancy authority; the stored count is
        // reconciled from them rather than trusted.
        var activeMemberCount = await CountActiveMembersAsync(id, cancellationToken);

        if (updateTeamDto.MaxMembers is { } newMaxMembers && newMaxMembers < activeMemberCount)
            throw new InvalidOperationException(
                $"MaxMembers cannot be lower than the team's current active member count ({activeMemberCount}).");

        if (!string.IsNullOrWhiteSpace(updateTeamDto.Name))
            team.Name = updateTeamDto.Name;

        if (updateTeamDto.Description != null)
            team.Description = updateTeamDto.Description;

        team.LifecycleStatus = lifecycleStatus;
        team.IsAcceptingMembers = isAcceptingMembers;

        // Capacity is derived (IsFull/OpenSlots) from MaxMembers and the member count; no
        // status is changed here.
        if (updateTeamDto.MaxMembers is { } maxMembers)
            team.MaxMembers = maxMembers;

        if (updateTeamDto.Region != null)
            team.Region = updateTeamDto.Region;

        if (updateTeamDto.Category != null)
            team.Category = updateTeamDto.Category;

        if (updateTeamDto.Tags != null)
            team.Tags = updateTeamDto.Tags;

        ReconcileMemberCount(team, activeMemberCount);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new InvalidOperationException(
                "The team changed while it was being updated. Please try again.",
                ex);
        }

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
        // Load the team first so its RowVersion predates the membership reads below.
        var team = await _context.Teams.FindAsync([teamId], cancellationToken);
        if (team == null) return false;

        var teamMember = await _context.TeamMembers
            .FirstOrDefaultAsync(tm => tm.TeamId == teamId && tm.PlayerId == playerId && tm.IsActive, cancellationToken);

        if (teamMember == null) return false;

        // Mark as inactive instead of deleting
        teamMember.IsActive = false;

        // Derive the exact remaining roster from the other active memberships instead of
        // decrementing the stored count, so a stale count is repaired and can never go negative.
        var remainingActiveCount = await _context.TeamMembers.CountAsync(
            tm => tm.TeamId == teamId && tm.IsActive && tm.PlayerId != playerId,
            cancellationToken);

        // The freed slot shows up in the derived OpenSlots/IsFull; no status is changed.
        ReconcileMemberCount(team, remainingActiveCount);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new InvalidOperationException(
                "The team changed while this member was leaving. Please try again.",
                ex);
        }

        return true;
    }

    private static IQueryable<Team> ApplyLegacyStatusFilter(IQueryable<Team> query, TeamStatus status) =>
        status switch
        {
            TeamStatus.Recruiting => query.Where(t =>
                t.LifecycleStatus == TeamLifecycleStatus.Active
                && t.IsAcceptingMembers
                && t.CurrentMemberCount < t.MaxMembers),
            TeamStatus.Full => query.Where(t =>
                t.LifecycleStatus == TeamLifecycleStatus.Active
                && t.CurrentMemberCount >= t.MaxMembers),
            TeamStatus.Active => query.Where(t =>
                t.LifecycleStatus == TeamLifecycleStatus.Active
                && !t.IsAcceptingMembers
                && t.CurrentMemberCount < t.MaxMembers),
            TeamStatus.Inactive => query.Where(t => t.LifecycleStatus == TeamLifecycleStatus.Inactive),
            TeamStatus.Disbanded => query.Where(t => t.LifecycleStatus == TeamLifecycleStatus.Disbanded),
            _ => query.Where(_ => false)
        };

    private Task<int> CountActiveMembersAsync(Guid teamId, CancellationToken cancellationToken) =>
        _context.TeamMembers.CountAsync(tm => tm.TeamId == teamId && tm.IsActive, cancellationToken);

    /// <summary>
    /// Sets the stored count to the authoritative active-membership count and always marks it
    /// modified, so the save issues an UPDATE guarded by Team.RowVersion even when the value is
    /// unchanged. That makes every capacity-dependent write conflict with any concurrent one.
    /// </summary>
    private void ReconcileMemberCount(Team team, int activeMemberCount)
    {
        team.CurrentMemberCount = activeMemberCount;
        _context.Entry(team).Property(t => t.CurrentMemberCount).IsModified = true;
    }

    private static TeamDto MapToDto(Team team)
    {
        return new TeamDto
        {
            Id = team.Id,
            Name = team.Name,
            Description = team.Description,
            LifecycleStatus = team.LifecycleStatus,
            IsAcceptingMembers = team.IsAcceptingMembers,
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
