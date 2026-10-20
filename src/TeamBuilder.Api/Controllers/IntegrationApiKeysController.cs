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
/// The caller's integration API keys, for other applications to read the caller's hosted games
/// through <c>/api/integration/v1</c>. Linked players only (no token 401, unlinked 403). The key
/// itself is returned once, by create; lists carry only its non-secret prefix. A caller can
/// only see and revoke their own keys (another player's key id is 404).
/// </summary>
[ApiController]
[Route("api/v1/players/me/api-keys")]
[Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
public class IntegrationApiKeysController : ControllerBase
{
    private readonly IIntegrationApiKeyService _keys;
    private readonly ICurrentPlayerResolver _currentPlayer;

    public IntegrationApiKeysController(IIntegrationApiKeyService keys, ICurrentPlayerResolver currentPlayer)
    {
        _keys = keys;
        _currentPlayer = currentPlayer;
    }

    /// <summary>The caller's keys, active first, then newest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<IntegrationApiKeyDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<IntegrationApiKeyDto>>> List(CancellationToken cancellationToken)
    {
        var playerId = await _currentPlayer.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();
        return Ok(await _keys.ListAsync(playerId.Value, cancellationToken));
    }

    /// <summary>
    /// Creates a key and returns it in <c>key</c>, once: store it now. 400 for a missing name, an
    /// unknown scope or an expiry outside 1 to 365 days; 409 ApiKeyLimitReached at 10 active
    /// keys; 429 when keys are created or revoked too often.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting(ApiKeyChangeRateLimitOptions.PolicyName)]
    [ProducesResponseType(typeof(CreatedIntegrationApiKeyDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<CreatedIntegrationApiKeyDto>> Create([FromBody] CreateIntegrationApiKeyDto request, CancellationToken cancellationToken)
    {
        var playerId = await _currentPlayer.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        try
        {
            var created = await _keys.CreateAsync(playerId.Value, request, cancellationToken);
            // The response holds a credential: never cache it anywhere.
            Response.Headers.CacheControl = "no-store";
            return StatusCode(StatusCodes.Status201Created, created);
        }
        catch (IntegrationApiKeyLimitException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Conflict",
                detail: ex.Message,
                extensions: new Dictionary<string, object?> { ["code"] = IntegrationApiKeyLimitException.Code });
        }
    }

    /// <summary>Revokes one of the caller's keys; it stops working at once. Idempotent 204; 404 for a key that is not the caller's.</summary>
    [HttpDelete("{keyId:guid}")]
    [EnableRateLimiting(ApiKeyChangeRateLimitOptions.PolicyName)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Revoke(Guid keyId, CancellationToken cancellationToken)
    {
        var playerId = await _currentPlayer.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();
        return await _keys.RevokeAsync(playerId.Value, keyId, cancellationToken) ? NoContent() : NotFound();
    }
}
