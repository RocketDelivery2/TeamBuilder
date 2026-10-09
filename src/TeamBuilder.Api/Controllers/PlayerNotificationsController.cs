using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TeamBuilder.Api.Auth;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;

namespace TeamBuilder.Api.Controllers;

/// <summary>
/// The caller's own in-app notifications. Linked players only (no token 401, unlinked 403);
/// there is no way to read another player's notifications.
/// </summary>
[ApiController]
[Route("api/v1/players/me/notifications")]
[Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
public class PlayerNotificationsController : ControllerBase
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    private readonly IInAppNotificationService _notifications;
    private readonly ICurrentPlayerResolver _currentPlayer;

    public PlayerNotificationsController(IInAppNotificationService notifications, ICurrentPlayerResolver currentPlayer)
    {
        _notifications = notifications;
        _currentPlayer = currentPlayer;
    }

    /// <summary>
    /// Newest first. Keyset paged: pass the returned <c>nextCursor</c> as <paramref name="cursor"/>
    /// (with the same <paramref name="unreadOnly"/>). Page size 1-50 (default 20; out of range
    /// falls back to the default). Invalid cursor 400.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(InAppNotificationPageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<InAppNotificationPageDto>> GetPage(
        [FromQuery] bool unreadOnly = false,
        [FromQuery] int pageSize = DefaultPageSize,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var playerId = await _currentPlayer.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        if (pageSize < 1 || pageSize > MaxPageSize) pageSize = DefaultPageSize;

        return Ok(await _notifications.GetPageAsync(playerId.Value, unreadOnly, pageSize, cursor, cancellationToken));
    }

    /// <summary>The unread badge count, without loading a page.</summary>
    [HttpGet("unread-count")]
    [ProducesResponseType(typeof(UnreadNotificationCountDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<UnreadNotificationCountDto>> GetUnreadCount(CancellationToken cancellationToken)
    {
        var playerId = await _currentPlayer.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        return Ok(new UnreadNotificationCountDto { UnreadCount = await _notifications.CountUnreadAsync(playerId.Value, cancellationToken) });
    }

    /// <summary>Idempotent: 204 whether or not it was already read. Another player's or a missing notification 404.</summary>
    [HttpPost("{notificationId}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkRead(Guid notificationId, CancellationToken cancellationToken)
    {
        var playerId = await _currentPlayer.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        return await _notifications.MarkReadAsync(playerId.Value, notificationId, cancellationToken) ? NoContent() : NotFound();
    }

    /// <summary>
    /// The caller opened the game from this notification: <c>via</c> is <c>push</c> (an OS or
    /// browser notification click) or <c>inApp</c>; <c>clickToOpenMs</c> is the client-measured
    /// time from the click to the game showing. Marks it read; idempotent 204 (only the first
    /// open counts). Another player's or a missing notification 404. Metrics only: it grants
    /// nothing and claims nothing.
    /// </summary>
    [HttpPost("{notificationId}/opened")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkOpened(Guid notificationId, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] NotificationOpenedDto? opened, CancellationToken cancellationToken)
    {
        var playerId = await _currentPlayer.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        return await _notifications.MarkOpenedAsync(playerId.Value, notificationId, opened ?? new NotificationOpenedDto(), cancellationToken) ? NoContent() : NotFound();
    }
}
