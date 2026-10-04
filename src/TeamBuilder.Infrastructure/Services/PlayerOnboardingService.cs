using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Persistence;

namespace TeamBuilder.Infrastructure.Services;

public class PlayerOnboardingService(TeamBuilderDbContext context) : IPlayerOnboardingService
{
    public const string IdentityAlreadyLinkedMessage = "This identity is already linked to a player.";

    private readonly TeamBuilderDbContext _context = context;

    public async Task<PlayerDto?> GetByExternalIdentityAsync(
        ExternalIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var candidate = await _context.PlayerIdentities
            .AsNoTracking()
            .Where(pi => pi.Issuer == identity.Issuer && pi.Subject == identity.Subject)
            .Select(pi => new { pi.Issuer, pi.Subject, pi.Player })
            .FirstOrDefaultAsync(cancellationToken);

        if (candidate == null ||
            !string.Equals(candidate.Issuer, identity.Issuer, StringComparison.Ordinal) ||
            !string.Equals(candidate.Subject, identity.Subject, StringComparison.Ordinal))
        {
            return null;
        }

        return MapToDto(candidate.Player);
    }

    public async Task<PlayerDto> OnboardAsync(
        ExternalIdentity identity,
        CreatePlayerDto createPlayerDto,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(createPlayerDto);

        // Pre-checks give the common case a clean answer; the unique indexes stay authoritative
        // for concurrent requests (see the catch below).
        if (await _context.PlayerIdentities.AnyAsync(
                pi => pi.Issuer == identity.Issuer && pi.Subject == identity.Subject,
                cancellationToken))
        {
            throw new InvalidOperationException(IdentityAlreadyLinkedMessage);
        }

        if (await _context.Players.AnyAsync(p => p.Username == createPlayerDto.Username, cancellationToken))
        {
            throw UsernameTaken(createPlayerDto.Username);
        }

        // Player.Id is always TeamBuilder-generated; the external subject is only stored on the link.
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

        var playerIdentity = new PlayerIdentity
        {
            Id = Guid.NewGuid(),
            PlayerId = player.Id,
            Player = player,
            Issuer = identity.Issuer,
            Subject = identity.Subject,
            Provider = identity.Provider,
            TenantId = identity.TenantId
        };

        _context.Players.Add(player);
        _context.PlayerIdentities.Add(playerIdentity);

        // One SaveChanges call: EF Core inserts both rows in a single transaction, so a failure
        // on either insert leaves neither row behind.
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (PlayerOnboardingConflictClassifier.IsDuplicateExternalIdentity(ex))
        {
            _context.ChangeTracker.Clear();
            throw new InvalidOperationException(IdentityAlreadyLinkedMessage, ex);
        }
        catch (DbUpdateException ex) when (PlayerOnboardingConflictClassifier.IsDuplicateUsername(ex))
        {
            _context.ChangeTracker.Clear();
            throw UsernameTaken(createPlayerDto.Username, ex);
        }

        return MapToDto(player);
    }

    // Same message as PlayerService.CreateAsync so username conflicts read identically.
    private static InvalidOperationException UsernameTaken(string username, Exception? inner = null)
        => new($"Player with username '{username}' already exists.", inner);

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
