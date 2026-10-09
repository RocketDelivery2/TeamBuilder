using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TeamBuilder.Api.Auth;
using TeamBuilder.Api.RateLimiting;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Application.Interfaces;

namespace TeamBuilder.Api.Controllers;

/// <summary>
/// "Notify me if a spot opens" for one requirement of one occurrence. Linked players only. A
/// subscription never joins the caller and never reserves capacity: when a spot opens the
/// caller gets an in-app notification and still has to claim it through the roster claim API.
/// </summary>
[ApiController]
[Route("api/v1/events/{occurrenceId}/roster/requirements/{requirementId}/subscription")]
[Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
[EnableRateLimiting(SubscriptionRateLimitOptions.PolicyName)]
public class RosterSubscriptionsController : ControllerBase
{
    private readonly IRosterSubscriptionService _subscriptions;
    private readonly ICurrentPlayerResolver _currentPlayer;
    private readonly ILogger<RosterSubscriptionsController> _logger;

    public RosterSubscriptionsController(
        IRosterSubscriptionService subscriptions,
        ICurrentPlayerResolver currentPlayer,
        ILogger<RosterSubscriptionsController> logger)
    {
        _subscriptions = subscriptions;
        _currentPlayer = currentPlayer;
        _logger = logger;
    }

    /// <summary>
    /// Idempotent subscribe: 201 when created, 200 with the existing subscription. Order:
    /// no token 401, unlinked 403, missing occurrence 404, requirement not on this occurrence
    /// 404, closed occurrence 409 OccurrenceClosed, caller already holds a live spot on the
    /// occurrence 409 AlreadyParticipating, too many changes 429.
    /// </summary>
    [HttpPut]
    [ProducesResponseType(typeof(RosterSubscriptionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(RosterSubscriptionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<RosterSubscriptionDto>> Subscribe(Guid occurrenceId, Guid requirementId, CancellationToken cancellationToken)
    {
        var playerId = await _currentPlayer.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        try
        {
            var result = await _subscriptions.SubscribeAsync(occurrenceId, requirementId, playerId.Value, cancellationToken);
            if (!result.Created)
                return Ok(result.Subscription);

            _logger.LogInformation("Player {PlayerId} subscribed to vacancies of requirement {RequirementId} on event {EventId}", playerId.Value, requirementId, occurrenceId);
            return StatusCode(StatusCodes.Status201Created, result.Subscription);
        }
        catch (EventOccurrenceNotFoundException)
        {
            return NotFound();
        }
        catch (RosterRequirementNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Idempotent unsubscribe: 204 whether or not a subscription existed. Allowed on a closed
    /// occurrence. Missing occurrence or foreign requirement 404.
    /// </summary>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Unsubscribe(Guid occurrenceId, Guid requirementId, CancellationToken cancellationToken)
    {
        var playerId = await _currentPlayer.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        try
        {
            await _subscriptions.UnsubscribeAsync(occurrenceId, requirementId, playerId.Value, cancellationToken);
            return NoContent();
        }
        catch (EventOccurrenceNotFoundException)
        {
            return NotFound();
        }
        catch (RosterRequirementNotFoundException)
        {
            return NotFound();
        }
    }
}
