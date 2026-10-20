namespace TeamBuilder.Domain.Entities;

/// <summary>
/// A credential another application uses to call the public integration API
/// (<c>/api/integration/v1</c>) on behalf of the <see cref="Player"/> who created it. The key
/// reads only what its owner hosts. Only a SHA-256 of the full key is stored; the plaintext is
/// shown once at creation and cannot be recovered. <see cref="KeyPrefix"/> is the public,
/// unique lookup part of the key (also shown in key lists so an owner can tell keys apart).
/// </summary>
public class IntegrationApiKey : BaseEntity
{
    public Guid PlayerId { get; set; }
    public Player Player { get; set; } = null!;

    /// <summary>The owner's label for the key, e.g. "League website".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The random, non-secret identifier embedded in the key (<c>tbk_{prefix}_{secret}</c>).</summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>SHA-256 of the full key string (32 bytes).</summary>
    public byte[] SecretHash { get; set; } = [];

    /// <summary>Granted scopes, space-separated (see <c>IntegrationScopes</c>).</summary>
    public string Scopes { get; set; } = string.Empty;

    /// <summary>When the key stops working on its own; null for no expiry.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>Last successful authentication, recorded at most once a minute.</summary>
    public DateTime? LastUsedAtUtc { get; set; }

    /// <summary>When the owner revoked the key. A revoked key never authenticates again.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    public bool IsActiveAt(DateTime utcNow) =>
        RevokedAtUtc is null && (ExpiresAtUtc is null || ExpiresAtUtc > utcNow);
}
