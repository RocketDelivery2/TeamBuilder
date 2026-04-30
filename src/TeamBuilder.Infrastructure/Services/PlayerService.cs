using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Infrastructure.Services;

public class PlayerService(TeamBuilderDbContext context) : IPlayerService
{
    private readonly TeamBuilderDbContext _context = context;

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

        _context.Players.Remove(player);
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

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