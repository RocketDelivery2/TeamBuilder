using TeamBuilder.Application.Models;

namespace TeamBuilder.Application.Interfaces;

/// <summary>
/// Resolves the external identity (Issuer + Subject) of the authenticated caller.
/// Unlike <see cref="ICurrentUserContext"/>, it does not assume the token carries a
/// TeamBuilder player GUID.
/// </summary>
public interface IExternalIdentityAccessor
{
    /// <summary>
    /// The caller's external identity, or <c>null</c> when the request is unauthenticated or the
    /// token lacks a usable issuer or subject.
    /// </summary>
    ExternalIdentity? Current { get; }
}
