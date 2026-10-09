using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Domain;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Outbox;
using TeamBuilder.Infrastructure.Persistence;

namespace TeamBuilder.Infrastructure.Services;

/// <summary>
/// "Notify me if a spot opens" on one requirement of one occurrence. A subscription is only an
/// alert preference: it never reserves capacity and never joins anyone.
/// </summary>
public class RosterSubscriptionService : IRosterSubscriptionService
{
    public const string AlreadyParticipatingMessage =
        "You already hold a spot in this game, so there is nothing to be notified about.";

    private readonly TeamBuilderDbContext _context;
    private readonly RefillLimitsOptions _limits;

    public RosterSubscriptionService(TeamBuilderDbContext context, IOptions<RefillLimitsOptions> limits)
    {
        _context = context;
        _limits = limits.Value;
    }

    public async Task<RosterSubscriptionResult> SubscribeAsync(Guid occurrenceId, Guid requirementId, Guid playerId, CancellationToken cancellationToken = default)
    {
        var status = await EnsureRequirementAsync(occurrenceId, requirementId, cancellationToken);

        var existing = await FindAsync(occurrenceId, requirementId, playerId, cancellationToken);
        if (existing is not null)
            return new RosterSubscriptionResult(existing, Created: false);

        if (!RosterState.AcceptsNewRosterMutations(status))
            throw new RosterConflictException(RosterConflictCodes.OccurrenceClosed, EventRosterService.ClosedOccurrenceMessage);

        var supplyStatuses = RosterState.SupplyStatuses;
        if (await _context.RosterAssignments.AnyAsync(
                a => a.OccurrenceId == occurrenceId && a.PlayerId == playerId && supplyStatuses.Contains(a.Status),
                cancellationToken))
        {
            throw new RosterConflictException(RosterConflictCodes.AlreadyParticipating, AlreadyParticipatingMessage);
        }

        // Fan-out guard per player (never per game): count alerts on games that can still change.
        var activeSubscriptions = await _context.OccurrenceRosterSubscriptions
            .Where(s => s.PlayerId == playerId)
            .Where(s => _context.Events.Any(e => e.Id == s.OccurrenceId &&
                e.Status != Domain.Enums.EventStatus.Completed &&
                e.Status != Domain.Enums.EventStatus.Cancelled &&
                e.Status != Domain.Enums.EventStatus.Archived))
            .CountAsync(cancellationToken);
        if (activeSubscriptions >= _limits.MaxActiveOccurrenceSubscriptionsPerPlayer)
        {
            throw new RosterConflictException(
                RosterConflictCodes.SubscriptionLimitReached,
                $"You already have {_limits.MaxActiveOccurrenceSubscriptionsPerPlayer} spot alerts on open games. Turn one off to add another.");
        }

        var subscription = new OccurrenceRosterSubscription
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            OccurrenceId = occurrenceId,
            RosterRequirementId = requirementId
        };
        _context.OccurrenceRosterSubscriptions.Add(subscription);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (RosterConflictClassifier.IsDuplicateRosterSubscription(ex))
        {
            // A concurrent identical request (double tap) committed first: same outcome.
            _context.ChangeTracker.Clear();
            var committed = await FindAsync(occurrenceId, requirementId, playerId, cancellationToken)
                ?? throw new InvalidOperationException("The subscription conflicted but could not be read back.", ex);
            return new RosterSubscriptionResult(committed, Created: false);
        }

        return new RosterSubscriptionResult(ToDto(subscription.OccurrenceId, subscription.RosterRequirementId, subscription.CreatedAtUtc), Created: true);
    }

    public async Task UnsubscribeAsync(Guid occurrenceId, Guid requirementId, Guid playerId, CancellationToken cancellationToken = default)
    {
        await EnsureRequirementAsync(occurrenceId, requirementId, cancellationToken);

        // Idempotent: nothing to delete is the same success, and so is losing a race to a
        // concurrent identical delete. Allowed on a closed occurrence: turning alerts off is
        // always fine.
        var subscription = await _context.OccurrenceRosterSubscriptions
            .FirstOrDefaultAsync(s => s.PlayerId == playerId && s.OccurrenceId == occurrenceId && s.RosterRequirementId == requirementId, cancellationToken);
        if (subscription is null)
            return;

        _context.OccurrenceRosterSubscriptions.Remove(subscription);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Already deleted by a concurrent request.
        }
    }

    /// <summary>404 for a missing occurrence, then 404 for a requirement not on it; returns the occurrence status.</summary>
    private async Task<Domain.Enums.EventStatus> EnsureRequirementAsync(Guid occurrenceId, Guid requirementId, CancellationToken cancellationToken)
    {
        var status = await _context.Events
            .Where(e => e.Id == occurrenceId)
            .Select(e => (Domain.Enums.EventStatus?)e.Status)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new EventOccurrenceNotFoundException(occurrenceId);

        if (!await _context.RosterRequirements.AnyAsync(r => r.Id == requirementId && r.OccurrenceId == occurrenceId, cancellationToken))
            throw new RosterRequirementNotFoundException(requirementId);

        return status;
    }

    private async Task<RosterSubscriptionDto?> FindAsync(Guid occurrenceId, Guid requirementId, Guid playerId, CancellationToken cancellationToken)
    {
        var createdAtUtc = await _context.OccurrenceRosterSubscriptions
            .AsNoTracking()
            .Where(s => s.PlayerId == playerId && s.OccurrenceId == occurrenceId && s.RosterRequirementId == requirementId)
            .Select(s => (DateTime?)s.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        return createdAtUtc is { } created ? ToDto(occurrenceId, requirementId, created) : null;
    }

    private static RosterSubscriptionDto ToDto(Guid occurrenceId, Guid requirementId, DateTime createdAtUtc) => new()
    {
        OccurrenceId = occurrenceId,
        RosterRequirementId = requirementId,
        Subscribed = true,
        CreatedAtUtc = EventService.AsUtc(createdAtUtc)
    };
}
