using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Infrastructure.Data;

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
