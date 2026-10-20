using TeamBuilder.Application.DTOs;

namespace TeamBuilder.Application.Interfaces;

/// <summary>
/// Integration API keys: owners create, list and revoke their own keys; the integration
/// authentication scheme resolves a presented key. Keys are stored only as a SHA-256 hash.
/// </summary>
public interface IIntegrationApiKeyService
{
    /// <summary>The most active keys one player may hold at once.</summary>
    const int MaxActiveKeysPerPlayer = 10;

    /// <summary>
    /// Creates a key for <paramref name="playerId"/> and returns it with its plaintext, once.
    /// </summary>
    /// <exception cref="Exceptions.IntegrationApiKeyLimitException">The player already holds the maximum of active keys.</exception>
    Task<CreatedIntegrationApiKeyDto> CreateAsync(Guid playerId, CreateIntegrationApiKeyDto request, CancellationToken cancellationToken = default);

    /// <summary>The player's keys, active first then newest first. Never returns a key or hash.</summary>
    Task<IReadOnlyList<IntegrationApiKeyDto>> ListAsync(Guid playerId, CancellationToken cancellationToken = default);

    /// <summary>Revokes one of the player's keys. False when the player has no key with that id. Idempotent.</summary>
    Task<bool> RevokeAsync(Guid playerId, Guid keyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a presented key to its identity: null for a malformed, unknown, revoked or
    /// expired key. Records last use at most once a minute.
    /// </summary>
    Task<IntegrationApiKeyIdentity?> AuthenticateAsync(string presentedKey, CancellationToken cancellationToken = default);

    /// <summary>The calling key and its owner, for <c>GET /api/integration/v1/me</c>.</summary>
    Task<IntegrationCallerDto?> DescribeAsync(Guid keyId, CancellationToken cancellationToken = default);
}
