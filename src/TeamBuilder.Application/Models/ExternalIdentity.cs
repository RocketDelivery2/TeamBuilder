namespace TeamBuilder.Application.Models;

/// <summary>
/// The external (identity provider) identity of an authenticated caller.
/// <see cref="Issuer"/> + <see cref="Subject"/> is the canonical key used to find the linked
/// TeamBuilder player; <see cref="Provider"/> and <see cref="TenantId"/> are metadata only.
/// <see cref="Subject"/> is an opaque string and is never used as a <c>Player.Id</c>.
/// </summary>
public sealed record ExternalIdentity(string Issuer, string Subject, string Provider, string? TenantId);
