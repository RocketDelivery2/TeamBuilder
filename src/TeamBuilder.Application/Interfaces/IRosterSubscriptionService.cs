using TeamBuilder.Application.DTOs;

namespace TeamBuilder.Application.Interfaces;

/// <summary>"Notify me if a spot opens" subscriptions, per occurrence requirement.</summary>
public interface IRosterSubscriptionService
{
    /// <summary>
    /// Idempotently subscribes the player. Missing occurrence: EventOccurrenceNotFoundException;
    /// requirement not on it: RosterRequirementNotFoundException; closed occurrence: 409
    /// OccurrenceClosed; the player already holds a live spot on the occurrence: 409
    /// AlreadyParticipating. An existing subscription is returned with Created = false.
    /// </summary>
    Task<RosterSubscriptionResult> SubscribeAsync(Guid occurrenceId, Guid requirementId, Guid playerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotently unsubscribes (no error when not subscribed). Missing occurrence or a
    /// requirement not on it throw the same not-found exceptions as <see cref="SubscribeAsync"/>.
    /// </summary>
    Task UnsubscribeAsync(Guid occurrenceId, Guid requirementId, Guid playerId, CancellationToken cancellationToken = default);
}
