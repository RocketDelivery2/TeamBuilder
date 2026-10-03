namespace TeamBuilder.Application.Interfaces;

/// <summary>
/// Resolves the internal TeamBuilder player ID of the authenticated caller from their external
/// identity. Issuer + Subject are exact external identity keys: they are matched ordinally, as
/// produced by <see cref="IExternalIdentityAccessor"/>, with no case folding or issuer URI
/// normalization (OpenID Connect compares issuers by exact string match).
/// </summary>
public interface ICurrentPlayerResolver
{
    /// <summary>
    /// Returns the <c>Player.Id</c> linked to the caller's external identity, or <c>null</c> when
    /// the caller has no authenticated external identity or the identity is not linked to a player.
    /// </summary>
    Task<Guid?> ResolvePlayerIdAsync(CancellationToken cancellationToken = default);
}
