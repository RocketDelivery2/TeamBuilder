using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamBuilder.Application.Outbox;
using TeamBuilder.Domain;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Data.Configurations;
using TeamBuilder.Infrastructure.Persistence;
using TeamBuilder.Infrastructure.WebPush;

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
/// <para>
/// When Web Push is enabled, the same commit also queues one <see cref="PushDelivery"/> per
/// active browser of each new recipient. The in-app notification stays the durable record and
/// the push is best effort: the message completes as soon as that commit succeeds, and the
/// <see cref="WebPush.PushDispatcher"/> sends (and retries) each device independently. A replay
/// creates no new notifications and therefore no new deliveries.
/// </para>
/// </summary>
public sealed class RosterVacancyNotificationHandler : IOutboxMessageHandler
{
    public const string NotificationType = "roster.vacancy";

    private const int MaxInsertAttempts = 3;

    private readonly TeamBuilderDbContext _context;
    private readonly ILogger<RosterVacancyNotificationHandler> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly WebPushOptions _webPush;
    private readonly PushDeliverySignal? _pushSignal;

    public RosterVacancyNotificationHandler(
        TeamBuilderDbContext context,
        ILogger<RosterVacancyNotificationHandler> logger,
        TimeProvider timeProvider,
        IOptions<WebPushOptions> webPush,
        PushDeliverySignal? pushSignal = null)
    {
        _context = context;
        _logger = logger;
        _timeProvider = timeProvider;
        _webPush = webPush.Value;
        _pushSignal = pushSignal;
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

            var devices = _webPush.Enabled
                ? await ActiveDevicesAsync(recipients, cancellationToken)
                : [];
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var queued = 0;

            foreach (var playerId in recipients)
            {
                var notificationId = Guid.NewGuid();
                foreach (var subscriptionId in devices.GetValueOrDefault(playerId) ?? [])
                {
                    _context.PushDeliveries.Add(new PushDelivery
                    {
                        Id = Guid.NewGuid(),
                        InAppNotificationId = notificationId,
                        PushSubscriptionId = subscriptionId,
                        Status = Domain.Enums.PushDeliveryStatus.Pending,
                        NextAttemptAtUtc = now,
                        SourceOccurredAtUtc = vacancy.OccurredAtUtc,
                        CreatedAtUtc = now
                    });
                    queued++;
                }

                _context.InAppNotifications.Add(new InAppNotification
                {
                    Id = notificationId,
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
            RefillTelemetry.VacancyToNotification.Record(RefillTelemetry.Milliseconds(vacancy.OccurredAtUtc, _timeProvider.GetUtcNow().UtcDateTime));
            _logger.LogInformation(
                "NotificationCreated: {Count} vacancy notification(s) and {Pushes} push delivery(ies) for event {EventId} on occurrence {OccurrenceId} requirement {RequirementId}.",
                recipients.Count, queued, vacancy.EventId, vacancy.OccurrenceId, vacancy.RosterRequirementId);
            if (queued > 0)
                _pushSignal?.Signal();
            return new OutboxHandlerResult(recipients.Count, "Notified");
        }
    }

    /// <summary>Active browser subscriptions of the recipients, by player (several devices each).</summary>
    private async Task<Dictionary<Guid, List<Guid>>> ActiveDevicesAsync(List<Guid> recipients, CancellationToken cancellationToken)
    {
        var devices = new Dictionary<Guid, List<Guid>>();
        // Chunked so a large fan-out stays well under SQL Server's parameter limit.
        foreach (var chunk in recipients.Chunk(500))
        {
            var rows = await _context.PushSubscriptions
                .AsNoTracking()
                .Where(s => s.IsActive && chunk.Contains(s.PlayerId))
                .Select(s => new { s.PlayerId, s.Id })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                if (!devices.TryGetValue(row.PlayerId, out var list))
                    devices[row.PlayerId] = list = [];
                list.Add(row.Id);
            }
        }
        return devices;
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
