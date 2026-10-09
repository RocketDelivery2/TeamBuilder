using TeamBuilder.Application.DTOs;

namespace TeamBuilder.Application.Interfaces;

/// <summary>The caller's own in-app notifications. Never readable by anyone else.</summary>
public interface IInAppNotificationService
{
    /// <summary>Newest first, keyset paged. An invalid cursor throws ArgumentException (400).</summary>
    Task<InAppNotificationPageDto> GetPageAsync(Guid playerId, bool unreadOnly, int pageSize, string? cursor, CancellationToken cancellationToken = default);

    Task<int> CountUnreadAsync(Guid playerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks one of the player's notifications read (a no-op when already read). False when it
    /// does not exist or belongs to another player, which callers report as 404.
    /// </summary>
    Task<bool> MarkReadAsync(Guid playerId, Guid notificationId, CancellationToken cancellationToken = default);
}
