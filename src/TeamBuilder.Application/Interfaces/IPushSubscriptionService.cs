using TeamBuilder.Application.DTOs;

namespace TeamBuilder.Application.Interfaces;

/// <summary>
/// The caller's own browser push subscriptions. Credentials go in and never come out; another
/// player's subscriptions are invisible (404).
/// </summary>
public interface IPushSubscriptionService
{
    WebPushConfigDto GetConfig();

    /// <summary>
    /// Idempotent register/update of this browser (keyed by endpoint). Invalid input throws
    /// ArgumentException (400) with a message that never echoes the values. Throws
    /// InvalidOperationException when Web Push is disabled.
    /// </summary>
    Task<PushSubscriptionRegistration> RegisterAsync(Guid playerId, RegisterPushSubscriptionDto request, string? userAgent, CancellationToken cancellationToken = default);

    /// <summary>Removes the caller's subscription for this endpoint; a no-op when there is none.</summary>
    Task UnregisterAsync(Guid playerId, string endpoint, CancellationToken cancellationToken = default);

    /// <summary>Removes one of the caller's subscriptions by id; false when it is not theirs (404).</summary>
    Task<bool> DeleteAsync(Guid playerId, Guid subscriptionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PushSubscriptionDto>> ListAsync(Guid playerId, CancellationToken cancellationToken = default);
}
