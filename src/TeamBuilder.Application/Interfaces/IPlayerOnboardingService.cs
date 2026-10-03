using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;

namespace TeamBuilder.Application.Interfaces;

/// <summary>
/// Looks up and creates the TeamBuilder player linked to an external identity.
/// </summary>
public interface IPlayerOnboardingService
{
    /// <summary>Returns the player linked to <paramref name="identity"/>, or <c>null</c> when none is linked.</summary>
    Task<PlayerDto?> GetByExternalIdentityAsync(ExternalIdentity identity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new player with a server-generated ID and links <paramref name="identity"/> to it,
    /// atomically. Throws <see cref="InvalidOperationException"/> when the identity is already linked
    /// or the username is taken.
    /// </summary>
    Task<PlayerDto> OnboardAsync(ExternalIdentity identity, CreatePlayerDto createPlayerDto, CancellationToken cancellationToken = default);
}
