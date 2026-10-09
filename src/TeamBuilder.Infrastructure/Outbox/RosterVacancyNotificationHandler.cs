using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TeamBuilder.Application.Outbox;
using TeamBuilder.Domain;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Data.Configurations;
using TeamBuilder.Infrastructure.Persistence;

namespace TeamBuilder.Infrastructure.Outbox;

/// <summary>
/// Consumer of <c>roster.vacancy.opened.v1</c>: creates in-app notifications for the players who
/// asked to be notified about that requirement.
/// <para>
/// The event only says capacity opened at some moment, so current state is re-read first and
/// the message completes without notifying anyone when the occurrence no longer accepts roster
/// changes, the requirement is gone, or it is already full again. Recipients are the current
/// subscribers of the requirement who still exist, hold no live spot on the occurrence, and are
/// not the player whose assignment opened the spot. A claim may still fill the spot right after
/// this check: notifications are advisory and the atomic claim stays the authority.
/// </para>
/// <para>
/// Idempotent: (SourceEventId, PlayerId) is unique, existing rows are skipped, and a duplicate
/// insert from a concurrent late worker is absorbed by re-reading.
/// </para>
/// </summary>
public sealed class RosterVacancyNotificationHandler : IOutboxMessageHandler
{
    public const string NotificationType = "roster.vacancy";

    private const int MaxInsertAttempts = 3;

    private readonly TeamBuilderDbContext _context;
    private readonly ILogger<RosterVacancyNotificationHandler> _logger;

    public RosterVacancyNotificationHandler(TeamBuilderDbContext context, ILogger<RosterVacancyNotificationHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public string Type => RosterVacancyOpenedV1.EventType;

    public async Task<OutboxHandlerResult> HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var vacancy = RosterVacancyOpenedV1.FromJson(message.PayloadJson);

        var occurrence = await _context.Events
            .AsNoTracking()
            .Where(e => e.Id == vacancy.OccurrenceId)
            .Select(e => new { e.Name, e.Category, e.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (occurrence is null)
            return new OutboxHandlerResult(0, "OccurrenceMissing");
        if (!RosterState.AcceptsNewRosterMutations(occurrence.Status))
            return new OutboxHandlerResult(0, "OccurrenceClosed");

        var supplyStatuses = RosterState.SupplyStatuses;
        var requirement = await _context.RosterRequirements
            .AsNoTracking()
            .Where(r => r.Id == vacancy.RosterRequirementId && r.OccurrenceId == vacancy.OccurrenceId)
            .Select(r => new
            {
                r.RoleCode,
                r.DisplayPosition,
                r.RequiredCount,
                Supply = _context.RosterAssignments.Count(a => a.RequirementId == r.Id && supplyStatuses.Contains(a.Status))
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (requirement is null)
            return new OutboxHandlerResult(0, "RequirementMissing");
        if (RosterState.OpenQuantity(requirement.RequiredCount, requirement.Supply) == 0)
            return new OutboxHandlerResult(0, "RequirementFull");

        var title = Truncate(TitleFor(occurrence.Category), InAppNotificationConfiguration.TitleMaxLength);
        var body = Truncate(BodyFor(requirement.RoleCode, requirement.DisplayPosition, occurrence.Name), InAppNotificationConfiguration.BodyMaxLength);

        for (var attempt = 1; ; attempt++)
        {
            var recipients = await FindRecipientsAsync(vacancy, cancellationToken);
            if (recipients.Count == 0)
            {
                var alreadyNotified = await _context.InAppNotifications.AnyAsync(n => n.SourceEventId == vacancy.EventId, cancellationToken);
                return new OutboxHandlerResult(0, alreadyNotified ? "AlreadyNotified" : "NoEligibleSubscribers");
            }

            foreach (var playerId in recipients)
            {
                _context.InAppNotifications.Add(new InAppNotification
                {
                    Id = Guid.NewGuid(),
                    PlayerId = playerId,
                    Type = NotificationType,
                    OccurrenceId = vacancy.OccurrenceId,
                    RosterRequirementId = vacancy.RosterRequirementId,
                    SourceEventId = vacancy.EventId,
                    Title = title,
                    Body = body
                });
            }

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (attempt < MaxInsertAttempts && RosterConflictClassifier.IsDuplicateSourceEventNotification(ex))
            {
                // Another worker (a reclaimed lease) inserted some of them first. Nothing of this
                // attempt was saved; re-read who still needs one.
                _context.ChangeTracker.Clear();
                continue;
            }

            RefillTelemetry.NotificationCreated.Add(recipients.Count, new KeyValuePair<string, object?>("type", NotificationType));
            _logger.LogInformation(
                "NotificationCreated: {Count} vacancy notification(s) for event {EventId} on occurrence {OccurrenceId} requirement {RequirementId}.",
                recipients.Count, vacancy.EventId, vacancy.OccurrenceId, vacancy.RosterRequirementId);
            return new OutboxHandlerResult(recipients.Count, "Notified");
        }
    }

    private async Task<List<Guid>> FindRecipientsAsync(RosterVacancyOpenedV1 vacancy, CancellationToken cancellationToken)
    {
        var supplyStatuses = RosterState.SupplyStatuses;
        var vacatedPlayerId = _context.RosterAssignments
            .Where(a => a.Id == vacancy.VacatedAssignmentId)
            .Select(a => (Guid?)a.PlayerId);

        return await _context.OccurrenceRosterSubscriptions
            .AsNoTracking()
            .Where(s => s.OccurrenceId == vacancy.OccurrenceId && s.RosterRequirementId == vacancy.RosterRequirementId)
            .Where(s => _context.Players.Any(p => p.Id == s.PlayerId))
            .Where(s => !_context.RosterAssignments.Any(a =>
                a.OccurrenceId == vacancy.OccurrenceId && a.PlayerId == s.PlayerId && supplyStatuses.Contains(a.Status)))
            .Where(s => !vacatedPlayerId.Contains(s.PlayerId))
            .Where(s => !_context.InAppNotifications.Any(n => n.SourceEventId == vacancy.EventId && n.PlayerId == s.PlayerId))
            .OrderBy(s => s.CreatedAtUtc)
            .ThenBy(s => s.Id)
            .Select(s => s.PlayerId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>"Basketball spot opened"; never who left.</summary>
    internal static string TitleFor(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return "Spot opened";

        var trimmed = category.Trim();
        return $"{char.ToUpper(trimmed[0], CultureInfo.InvariantCulture)}{trimmed[1..]} spot opened";
    }

    /// <summary>"A participant spot opened in Wednesday Basketball."; never who left or why.</summary>
    internal static string BodyFor(string roleCode, string? displayPosition, string occurrenceName)
    {
        var role = string.IsNullOrWhiteSpace(displayPosition) ? roleCode : displayPosition.Trim();
        var article = role.Length > 0 && "aeiouAEIOU".Contains(role[0]) ? "An" : "A";
        return $"{article} {role} spot opened in {occurrenceName}.";
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
