using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Infrastructure.Services;

/// <summary>
/// Integration API keys (see <see cref="IntegrationApiKeyFormat"/>). The plaintext key leaves
/// this service exactly once, in the creation response, and is never logged. The active key
/// limit is checked before insert, so two simultaneous creates can briefly exceed it by one;
/// a key's power is bounded by its owner's own games, so that is not a privilege boundary.
/// </summary>
public class IntegrationApiKeyService : IIntegrationApiKeyService
{
    /// <summary>Last use is written at most this often per key, so reads do not become writes.</summary>
    internal static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    private readonly TeamBuilderDbContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<IntegrationApiKeyService> _logger;

    public IntegrationApiKeyService(TeamBuilderDbContext context, TimeProvider timeProvider, ILogger<IntegrationApiKeyService> logger)
    {
        _context = context;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<CreatedIntegrationApiKeyDto> CreateAsync(Guid playerId, CreateIntegrationApiKeyDto request, CancellationToken cancellationToken = default)
    {
        var now = UtcNow();
        var active = await _context.IntegrationApiKeys.CountAsync(
            k => k.PlayerId == playerId && k.RevokedAtUtc == null && (k.ExpiresAtUtc == null || k.ExpiresAtUtc > now),
            cancellationToken);
        if (active >= IIntegrationApiKeyService.MaxActiveKeysPerPlayer)
            throw new IntegrationApiKeyLimitException(IIntegrationApiKeyService.MaxActiveKeysPerPlayer);

        var (key, prefix) = IntegrationApiKeyFormat.Generate();
        var scopes = (request.Scopes ?? IntegrationScopes.Default).Distinct(StringComparer.Ordinal).ToList();
        var entity = new IntegrationApiKey
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            Name = request.Name!.Trim(),
            KeyPrefix = prefix,
            SecretHash = IntegrationApiKeyFormat.Hash(key),
            Scopes = IntegrationScopes.Join(scopes),
            ExpiresAtUtc = request.ExpiresInDays is { } days ? now.AddDays(days) : null,
            CreatedAtUtc = now
        };
        _context.IntegrationApiKeys.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Player {PlayerId} created integration API key {KeyId}.", playerId, entity.Id);

        var dto = new CreatedIntegrationApiKeyDto { Key = key };
        Fill(dto, entity, now);
        return dto;
    }

    public async Task<IReadOnlyList<IntegrationApiKeyDto>> ListAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        var now = UtcNow();
        var keys = await _context.IntegrationApiKeys
            .AsNoTracking()
            .Where(k => k.PlayerId == playerId)
            .ToListAsync(cancellationToken);
        return keys
            .Select(k => Fill(new IntegrationApiKeyDto(), k, now))
            .OrderByDescending(k => k.IsActive)
            .ThenByDescending(k => k.CreatedAtUtc)
            .ToList();
    }

    public async Task<bool> RevokeAsync(Guid playerId, Guid keyId, CancellationToken cancellationToken = default)
    {
        var key = await _context.IntegrationApiKeys
            .FirstOrDefaultAsync(k => k.Id == keyId && k.PlayerId == playerId, cancellationToken);
        if (key is null)
            return false;
        if (key.RevokedAtUtc is not null)
            return true;

        key.RevokedAtUtc = UtcNow();
        key.UpdatedAtUtc = key.RevokedAtUtc;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent last-used write or revoke touched the row; revoke against the current row.
            _context.ChangeTracker.Clear();
            return await RevokeAsync(playerId, keyId, cancellationToken);
        }

        _logger.LogInformation("Player {PlayerId} revoked integration API key {KeyId}.", playerId, keyId);
        return true;
    }

    public async Task<IntegrationApiKeyIdentity?> AuthenticateAsync(string presentedKey, CancellationToken cancellationToken = default)
    {
        if (!IntegrationApiKeyFormat.TryParse(presentedKey, out var prefix))
            return null;

        var key = await _context.IntegrationApiKeys.FirstOrDefaultAsync(k => k.KeyPrefix == prefix, cancellationToken);
        var now = UtcNow();
        if (key is null || !IntegrationApiKeyFormat.Matches(presentedKey, key.SecretHash) || !key.IsActiveAt(now))
            return null;

        if (key.LastUsedAtUtc is null || now - key.LastUsedAtUtc >= LastUsedResolution)
        {
            key.LastUsedAtUtc = now;
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another request recorded the use (or the owner revoked the key) in between.
                // Last use is advisory; a revoke wins on the next request.
                _context.ChangeTracker.Clear();
            }
        }

        return new IntegrationApiKeyIdentity(key.Id, key.PlayerId, IntegrationScopes.Split(key.Scopes));
    }

    public async Task<IntegrationCallerDto?> DescribeAsync(Guid keyId, CancellationToken cancellationToken = default)
    {
        return await _context.IntegrationApiKeys
            .AsNoTracking()
            .Where(k => k.Id == keyId)
            .Select(k => new IntegrationCallerDto
            {
                KeyId = k.Id,
                KeyName = k.Name,
                Scopes = IntegrationScopes.Split(k.Scopes),
                ExpiresAtUtc = k.ExpiresAtUtc,
                PlayerId = k.PlayerId,
                DisplayName = k.Player.DisplayName ?? k.Player.Username
            })
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static T Fill<T>(T dto, IntegrationApiKey key, DateTime now) where T : IntegrationApiKeyDto
    {
        dto.Id = key.Id;
        dto.Name = key.Name;
        dto.KeyPrefix = IntegrationApiKeyFormat.Display(key.KeyPrefix);
        dto.Scopes = IntegrationScopes.Split(key.Scopes);
        dto.CreatedAtUtc = Utc(key.CreatedAtUtc);
        dto.ExpiresAtUtc = Utc(key.ExpiresAtUtc);
        dto.LastUsedAtUtc = Utc(key.LastUsedAtUtc);
        dto.RevokedAtUtc = Utc(key.RevokedAtUtc);
        dto.IsActive = key.IsActiveAt(now);
        return dto;
    }

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? Utc(DateTime? value) => value is { } v ? Utc(v) : null;
}
