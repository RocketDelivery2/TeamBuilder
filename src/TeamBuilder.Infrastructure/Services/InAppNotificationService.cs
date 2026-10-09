using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Outbox;

namespace TeamBuilder.Infrastructure.Services;

/// <summary>
/// The caller's in-app notifications: newest first, keyset paged on (CreatedAtUtc, Id), which
/// both timeline indexes (all, and unread only) are ordered by.
/// </summary>
public class InAppNotificationService : IInAppNotificationService
{
    private readonly TeamBuilderDbContext _context;
    private readonly TimeProvider _timeProvider;

    public InAppNotificationService(TeamBuilderDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<InAppNotificationPageDto> GetPageAsync(Guid playerId, bool unreadOnly, int pageSize, string? cursor, CancellationToken cancellationToken = default)
    {
        var after = cursor is null ? (NotificationCursor?)null : NotificationCursor.Parse(cursor);

        var query = _context.InAppNotifications.AsNoTracking().Where(n => n.PlayerId == playerId);
        if (unreadOnly)
            query = query.Where(n => n.ReadAtUtc == null);
        if (after is { } position)
        {
            query = query.Where(n =>
                n.CreatedAtUtc < position.CreatedAtUtc ||
                (n.CreatedAtUtc == position.CreatedAtUtc && n.Id.CompareTo(position.Id) < 0));
        }

        var rows = await query
            .OrderByDescending(n => n.CreatedAtUtc)
            .ThenByDescending(n => n.Id)
            .Take(pageSize + 1)
            .Select(n => new InAppNotificationDto
            {
                Id = n.Id,
                Type = n.Type,
                OccurrenceId = n.OccurrenceId,
                RosterRequirementId = n.RosterRequirementId,
                Title = n.Title,
                Body = n.Body,
                CreatedAtUtc = n.CreatedAtUtc,
                ReadAtUtc = n.ReadAtUtc
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);

        foreach (var row in rows)
        {
            row.CreatedAtUtc = EventService.AsUtc(row.CreatedAtUtc);
            row.ReadAtUtc = row.ReadAtUtc is { } read ? EventService.AsUtc(read) : null;
        }

        return new InAppNotificationPageDto
        {
            Items = rows,
            NextCursor = hasMore ? new NotificationCursor(rows[^1].CreatedAtUtc, rows[^1].Id).Encode() : null
        };
    }

    public Task<int> CountUnreadAsync(Guid playerId, CancellationToken cancellationToken = default) =>
        _context.InAppNotifications.CountAsync(n => n.PlayerId == playerId && n.ReadAtUtc == null, cancellationToken);

    public async Task<bool> MarkReadAsync(Guid playerId, Guid notificationId, CancellationToken cancellationToken = default)
    {
        var notification = await _context.InAppNotifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.PlayerId == playerId, cancellationToken);
        if (notification is null)
            return false;

        if (notification.ReadAtUtc is null)
        {
            notification.ReadAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Marked read concurrently (another tab): already the desired state.
            }
        }

        return true;
    }

    /// <summary>A click-to-open time beyond this is a stale tab or a bad clock, not a measurement.</summary>
    internal const int MaxClickToOpenMs = 10 * 60 * 1000;

    /// <summary>Only notifications opened within this window count toward a later claim attempt.</summary>
    internal static readonly TimeSpan ClaimAttributionWindow = TimeSpan.FromHours(6);

    public async Task<bool> MarkOpenedAsync(Guid playerId, Guid notificationId, NotificationOpenedDto opened, CancellationToken cancellationToken = default)
    {
        var notification = await _context.InAppNotifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.PlayerId == playerId, cancellationToken);
        if (notification is null)
            return false;

        var via = string.Equals(opened.Via, "push", StringComparison.OrdinalIgnoreCase) ? "push" : "inApp";
        if (notification.OpenedAtUtc is null)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            notification.OpenedAtUtc = now;
            notification.ReadAtUtc ??= now;
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Opened concurrently in another tab: already recorded.
                return true;
            }

            RefillTelemetry.NotificationOpened.Add(1, new KeyValuePair<string, object?>("via", via));
            if (via == "push" && opened.ClickToOpenMs is >= 0 and <= MaxClickToOpenMs)
                RefillTelemetry.PushClickToGameOpen.Record(opened.ClickToOpenMs.Value);
        }

        return true;
    }

    public async Task RecordClaimAttemptAsync(Guid playerId, Guid occurrenceId, Guid requirementId, bool succeeded, CancellationToken cancellationToken = default)
    {
        try
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var since = now - ClaimAttributionWindow;
            var opened = await _context.InAppNotifications
                .AsNoTracking()
                .Where(n => n.PlayerId == playerId && n.OccurrenceId == occurrenceId && n.OpenedAtUtc != null && n.OpenedAtUtc >= since)
                .OrderByDescending(n => n.OpenedAtUtc)
                .Select(n => new
                {
                    n.OpenedAtUtc,
                    n.RosterRequirementId,
                    VacancyAtUtc = _context.OutboxMessages.Where(m => m.Id == n.SourceEventId).Select(m => (DateTime?)m.OccurredAtUtc).FirstOrDefault()
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (opened?.OpenedAtUtc is not { } openedAt)
                return;

            RefillTelemetry.GameOpenToClaimAttempt.Record(
                RefillTelemetry.Milliseconds(openedAt, now),
                new KeyValuePair<string, object?>("outcome", succeeded ? "claimed" : "conflict"));
            if (succeeded && opened.RosterRequirementId == requirementId && opened.VacancyAtUtc is { } vacancyAt)
                RefillTelemetry.VacancyToReplacement.Record(RefillTelemetry.Milliseconds(vacancyAt, now));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Metrics only: a failure here must never surface on the claim.
        }
    }
}

/// <summary>Keyset position (CreatedAtUtc, Id) of the last notification of a page; opaque base64url.</summary>
internal readonly record struct NotificationCursor(DateTime CreatedAtUtc, Guid Id)
{
    public string Encode()
    {
        var raw = $"n1:{CreatedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture)}:{Id:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <exception cref="ArgumentException">The cursor was not produced by <see cref="Encode"/>.</exception>
    public static NotificationCursor Parse(string cursor)
    {
        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(base64)).Split(':');
            if (parts.Length == 3 && parts[0] == "n1" &&
                long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) &&
                ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks &&
                Guid.TryParseExact(parts[2], "N", out var id))
            {
                return new NotificationCursor(new DateTime(ticks, DateTimeKind.Utc), id);
            }
        }
        catch (FormatException)
        {
        }

        throw new ArgumentException("The cursor is not valid.");
    }
}
