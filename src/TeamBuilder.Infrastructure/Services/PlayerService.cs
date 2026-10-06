using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Persistence;

namespace TeamBuilder.Infrastructure.Services;

public class PlayerService(TeamBuilderDbContext context) : IPlayerService
{
    private readonly TeamBuilderDbContext _context = context;

    public async Task<PublicPlayerDto?> GetPublicByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await _context.Players
            .Where(player => player.Id == id)
            .Select(player => new PublicPlayerDto
            {
                Id = player.Id,
                Username = player.Username,
                DisplayName = player.DisplayName,
                Bio = player.Bio,
                Region = player.Region,
                AvatarUrl = player.AvatarUrl,
                CreatedAtUtc = player.CreatedAtUtc,
                UpdatedAtUtc = player.UpdatedAtUtc
            })
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<PublicPlayerDto?> GetPublicByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        return await _context.Players
            .Where(player => player.Username == username)
            .Select(player => new PublicPlayerDto
            {
                Id = player.Id,
                Username = player.Username,
                DisplayName = player.DisplayName,
                Bio = player.Bio,
                Region = player.Region,
                AvatarUrl = player.AvatarUrl,
                CreatedAtUtc = player.CreatedAtUtc,
                UpdatedAtUtc = player.UpdatedAtUtc
            })
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<PaginatedResult<PublicPlayerDto>> GetPublicPlayersAsync(
        int page,
        int pageSize,
        string? region = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _context.Players.AsQueryable();

        if (!string.IsNullOrWhiteSpace(region))
        {
            query = query.Where(player => player.Region == region);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var players = await query
            .OrderByDescending(player => player.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(player => new PublicPlayerDto
            {
                Id = player.Id,
                Username = player.Username,
                DisplayName = player.DisplayName,
                Bio = player.Bio,
                Region = player.Region,
                AvatarUrl = player.AvatarUrl,
                CreatedAtUtc = player.CreatedAtUtc,
                UpdatedAtUtc = player.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return new PaginatedResult<PublicPlayerDto>
        {
            Items = players,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<PlayerDto?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var player = await _context.Players.FindAsync([id], cancellationToken);

        return player == null ? null : MapToDto(player);
    }

    public async Task<PlayerDto?> GetByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        var player = await _context.Players
            .FirstOrDefaultAsync(p => p.Username == username, cancellationToken);

        return player == null ? null : MapToDto(player);
    }

    public async Task<PaginatedResult<PlayerDto>> GetAllAsync(
        int page,
        int pageSize,
        string? region = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _context.Players.AsQueryable();

        if (!string.IsNullOrWhiteSpace(region))
        {
            query = query.Where(p => p.Region == region);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var players = await query
            .OrderByDescending(p => p.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PaginatedResult<PlayerDto>
        {
            Items = players.Select(MapToDto),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<PlayerDto> CreateAsync(
        CreatePlayerDto createPlayerDto,
        CancellationToken cancellationToken = default)
    {
        var existingPlayer = await _context.Players
            .FirstOrDefaultAsync(
                p => p.Username == createPlayerDto.Username,
                cancellationToken);

        if (existingPlayer != null)
        {
            throw new InvalidOperationException(
                $"Player with username '{createPlayerDto.Username}' already exists.");
        }

        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = createPlayerDto.Username,
            Email = createPlayerDto.Email,
            DisplayName = createPlayerDto.DisplayName,
            Bio = createPlayerDto.Bio,
            Region = createPlayerDto.Region,
            AvatarUrl = createPlayerDto.AvatarUrl
        };

        _context.Players.Add(player);
        await _context.SaveChangesAsync(cancellationToken);

        return MapToDto(player);
    }

    public async Task<PlayerDto?> UpdateAsync(
        Guid id,
        UpdatePlayerDto updatePlayerDto,
        CancellationToken cancellationToken = default)
    {
        var player = await _context.Players.FindAsync([id], cancellationToken);

        if (player == null)
        {
            return null;
        }

        if (updatePlayerDto.Email != null)
        {
            player.Email = updatePlayerDto.Email;
        }

        if (updatePlayerDto.DisplayName != null)
        {
            player.DisplayName = updatePlayerDto.DisplayName;
        }

        if (updatePlayerDto.Bio != null)
        {
            player.Bio = updatePlayerDto.Bio;
        }

        if (updatePlayerDto.Region != null)
        {
            player.Region = updatePlayerDto.Region;
        }

        if (updatePlayerDto.AvatarUrl != null)
        {
            player.AvatarUrl = updatePlayerDto.AvatarUrl;
        }

        await _context.SaveChangesAsync(cancellationToken);

        return MapToDto(player);
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var player = await _context.Players.FindAsync([id], cancellationToken);

        if (player == null)
        {
            return false;
        }

        // Ownership is never transferred or dissolved implicitly: the owner must delete their
        // teams first. (There is no ownership transfer endpoint yet.)
        var ownsTeams = await _context.Teams.AnyAsync(t => t.OwnerId == id, cancellationToken);
        if (ownsTeams)
        {
            throw new InvalidOperationException(OwnedTeamsDeletionConflictMessage);
        }

        // Event participation history is never erased or rewritten by a player delete; there is
        // no anonymization flow yet, so a player with any roster assignment cannot be deleted.
        if (await _context.RosterAssignments.AnyAsync(a => a.PlayerId == id, cancellationToken))
        {
            throw new InvalidOperationException(RosterHistoryDeletionConflictMessage);
        }

        // Every membership row (active and inactive history) is deleted explicitly: the
        // TeamMember -> Player FK no longer cascades, so a membership inserted after this query
        // makes the player delete fail instead of being silently removed uncounted.
        var memberships = await _context.TeamMembers
            .Where(tm => tm.PlayerId == id)
            .ToListAsync(cancellationToken);

        // Reconcile every team where this player holds an active membership. Teams (and their
        // RowVersions) are loaded before the counts so a concurrent membership change fails
        // the save instead of leaving a stale count.
        var affectedTeamIds = memberships
            .Where(tm => tm.IsActive)
            .Select(tm => tm.TeamId)
            .Distinct()
            .ToList();

        if (affectedTeamIds.Count > 0)
        {
            var affectedTeams = await _context.Teams
                .Where(t => affectedTeamIds.Contains(t.Id))
                .ToListAsync(cancellationToken);

            // The player became an owner after the guard above; never let the delete proceed.
            if (affectedTeams.Any(t => t.OwnerId == id))
            {
                throw new InvalidOperationException(OwnedTeamsDeletionConflictMessage);
            }

            var remainingCounts = await _context.TeamMembers
                .Where(tm => affectedTeamIds.Contains(tm.TeamId) && tm.IsActive && tm.PlayerId != id)
                .GroupBy(tm => tm.TeamId)
                .Select(g => new { TeamId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.TeamId, x => x.Count, cancellationToken);

            foreach (var team in affectedTeams)
            {
                var remainingActiveCount = remainingCounts.GetValueOrDefault(team.Id);

                team.CurrentMemberCount = remainingActiveCount;
                // Always issue the RowVersion-guarded UPDATE, even when the value is unchanged.
                _context.Entry(team).Property(t => t.CurrentMemberCount).IsModified = true;
            }
        }

        _context.TeamMembers.RemoveRange(memberships);
        _context.Players.Remove(player);

        // One SaveChanges: the team count corrections, the membership deletes and the player
        // delete commit or roll back together in EF Core's implicit transaction. The NO ACTION
        // foreign keys reject the player delete if an ownership or membership appeared after
        // the queries above; only those two expected races become a 409.
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new InvalidOperationException(
                "A team this player belongs to changed while the player was being deleted. Please try again.",
                ex);
        }
        catch (DbUpdateException ex) when (PlayerDeletionConflictClassifier.IsOwnedTeamReference(ex))
        {
            throw new InvalidOperationException(OwnedTeamsDeletionConflictMessage, ex);
        }
        catch (DbUpdateException ex) when (PlayerDeletionConflictClassifier.IsTeamMembershipReference(ex))
        {
            throw new InvalidOperationException(
                "This player joined a team while being deleted. Please try again.",
                ex);
        }

        catch (DbUpdateException ex) when (PlayerDeletionConflictClassifier.IsRosterAssignmentReference(ex))
        {
            throw new InvalidOperationException(RosterHistoryDeletionConflictMessage, ex);
        }

        return true;
    }

    private const string RosterHistoryDeletionConflictMessage =
        "This player has event roster history and cannot be deleted.";

    private const string OwnedTeamsDeletionConflictMessage =
        "Delete or transfer ownership of all owned teams before deleting this player.";

    private static PlayerDto MapToDto(Player player)
    {
        return new PlayerDto
        {
            Id = player.Id,
            Username = player.Username,
            Email = player.Email,
            DisplayName = player.DisplayName,
            Bio = player.Bio,
            Region = player.Region,
            AvatarUrl = player.AvatarUrl,
            CreatedAtUtc = player.CreatedAtUtc,
            UpdatedAtUtc = player.UpdatedAtUtc
        };
    }
}