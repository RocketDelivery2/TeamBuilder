namespace TeamBuilder.Domain.Entities;

/// <summary>
/// Links an external identity (an identity provider account) to an internal TeamBuilder player.
/// The canonical key of the external identity is <see cref="Issuer"/> + <see cref="Subject"/>;
/// <see cref="Player.Id"/> stays TeamBuilder-owned and is never derived from external claims.
/// </summary>
public class PlayerIdentity : BaseEntity
{
    public Guid PlayerId { get; set; }
    public Player Player { get; set; } = null!;

    /// <summary>The token issuer (the <c>iss</c> claim), compared ordinally.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// The issuer-scoped, stable user identifier, compared ordinally. For Microsoft Entra this
    /// is the <c>oid</c> claim, not the per-application <c>sub</c> claim.
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Descriptive provider name (e.g. "entra"). Metadata only; not part of the identity key.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Optional tenant identifier (e.g. the Entra <c>tid</c> claim). Metadata only.</summary>
    public string? TenantId { get; set; }
}
