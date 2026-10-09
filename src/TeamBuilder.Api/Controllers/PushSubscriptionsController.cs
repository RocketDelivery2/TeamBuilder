using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TeamBuilder.Api.Auth;
using TeamBuilder.Api.RateLimiting;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;

namespace TeamBuilder.Api.Controllers;

/// <summary>
/// The caller's browsers registered for Web Push ("Notify me when a spot opens" outside the
/// app). Linked players only (no token 401, unlinked 403). Subscription credentials are
/// accepted here and never returned: responses carry an id, a coarse browser family and
/// timestamps. Web Push is delivery only; a registration changes nothing on any roster.
/// </summary>
[ApiController]
[Route("api/v1/players/me/push-subscriptions")]
[Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
public class PushSubscriptionsController : ControllerBase
{
    private readonly IPushSubscriptionService _subscriptions;
    private readonly ICurrentPlayerResolver _currentPlayer;
    private readonly ILogger<PushSubscriptionsController> _logger;

    public PushSubscriptionsController(IPushSubscriptionService subscriptions, ICurrentPlayerResolver currentPlayer, ILogger<PushSubscriptionsController> logger)
    {
        _subscriptions = subscriptions;
        _currentPlayer = currentPlayer;
        _logger = logger;
    }

    /// <summary>
    /// Whether the server sends Web Push, and the VAPID public key to subscribe with. Public
    /// (the key is public by design); the client hides the browser-alert option when disabled.
    /// </summary>
    [HttpGet("/api/v1/push/config")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(WebPushConfigDto), StatusCodes.Status200OK)]
    public ActionResult<WebPushConfigDto> GetConfig() => Ok(_subscriptions.GetConfig());

    /// <summary>The caller's browsers, active first. No endpoints or keys.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PushSubscriptionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<PushSubscriptionDto>>> List(CancellationToken cancellationToken)
    {
        var playerId = await _currentPlayer.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();
        return Ok(await _subscriptions.ListAsync(playerId.Value, cancellationToken));
    }

    /// <summary>
    /// Registers or refreshes this browser (the body is <c>PushSubscription.toJSON()</c>, plus
    /// an optional <c>previousEndpoint</c> to retire after the browser rotated it). Idempotent:
    /// 201 when new, 200 when this endpoint was already registered (keys and last-seen are
    /// refreshed). 400 for an endpoint that is not a known push service or malformed keys;
    /// 409 WebPushDisabled when the server has no VAPID identity; 429 when changed too often.
    /// </summary>
    [HttpPut]
    [EnableRateLimiting(PushSubscriptionRateLimitOptions.PolicyName)]
    [ProducesResponseType(typeof(PushSubscriptionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(PushSubscriptionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PushSubscriptionDto>> Register([FromBody] RegisterPushSubscriptionDto request, CancellationToken cancellationToken)
    {
        var playerId = await _currentPlayer.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        if (!_subscriptions.GetConfig().Enabled)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Conflict",
                detail: Infrastructure.Services.PushSubscriptionService.WebPushDisabledMessage,
                extensions: new Dictionary<string, object?> { ["code"] = "WebPushDisabled" });
        }

        PushSubscriptionRegistration result;
        try
        {
            result = await _subscriptions.RegisterAsync(playerId.Value, request, Request.Headers.UserAgent.ToString(), cancellationToken);
        }
        catch (ArgumentException ex)
        {
            // The messages are fixed text; the submitted endpoint and keys are never echoed or logged.
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Bad Request", detail: ex.Message);
        }

        if (!result.Created)
            return Ok(result.Subscription);

        _logger.LogInformation("Player {PlayerId} registered push subscription {SubscriptionId} ({Family}).",
            playerId.Value, result.Subscription.Id, result.Subscription.UserAgentFamily);
        return StatusCode(StatusCodes.Status201Created, result.Subscription);
    }

    /// <summary>Turns browser alerts off for this endpoint and deletes its credentials. Idempotent 204.</summary>
    [HttpPost("unregister")]
    [EnableRateLimiting(PushSubscriptionRateLimitOptions.PolicyName)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Unregister([FromBody] UnregisterPushSubscriptionDto request, CancellationToken cancellationToken)
    {
        var playerId = await _currentPlayer.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();
        if (string.IsNullOrWhiteSpace(request.Endpoint))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Bad Request", detail: "endpoint is required.");

        await _subscriptions.UnregisterAsync(playerId.Value, request.Endpoint, cancellationToken);
        return NoContent();
    }

    /// <summary>Removes one of the caller's browsers by id (e.g. a lost phone). Another player's id 404.</summary>
    [HttpDelete("{subscriptionId:guid}")]
    [EnableRateLimiting(PushSubscriptionRateLimitOptions.PolicyName)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Delete(Guid subscriptionId, CancellationToken cancellationToken)
    {
        var playerId = await _currentPlayer.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();
        return await _subscriptions.DeleteAsync(playerId.Value, subscriptionId, cancellationToken) ? NoContent() : NotFound();
    }
}
