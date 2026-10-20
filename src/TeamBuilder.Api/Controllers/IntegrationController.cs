using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TeamBuilder.Api.Auth;
using TeamBuilder.Api.Hosting;
using TeamBuilder.Api.RateLimiting;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;

namespace TeamBuilder.Api.Controllers;

/// <summary>
/// The public integration API, v1 (contract <c>teambuilder.roster.v1</c>): read-only access for
/// other applications to the games an API key's owner hosts. Authenticated by an integration API
/// key in <c>X-Api-Key</c> only (401 without a valid, active key; 403 without the
/// <c>rosters:read</c> scope); rate limited per key (429 with Retry-After). A game hosted by
/// anyone else is 404, exactly like a missing one. Documented in docs/integration-api.md and
/// published as OpenAPI at <c>/api/integration/v1/openapi.json</c> in every environment.
/// </summary>
[ApiController]
[Route("api/integration/v1")]
[Authorize(Policy = IntegrationApiKeyAuthentication.RostersReadPolicy)]
[EnableRateLimiting(IntegrationRateLimitOptions.PolicyName)]
[ApiExplorerSettings(GroupName = IntegrationOpenApi.DocumentName)]
[Produces("application/json")]
public class IntegrationController : ControllerBase
{
    internal const int DefaultPageSize = 50;
    internal const int MaxPageSize = 100;
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);

    private readonly IIntegrationRosterService _rosters;
    private readonly IIntegrationApiKeyService _keys;
    private readonly TimeProvider _timeProvider;

    public IntegrationController(IIntegrationRosterService rosters, IIntegrationApiKeyService keys, TimeProvider timeProvider)
    {
        _rosters = rosters;
        _keys = keys;
        _timeProvider = timeProvider;
    }

    /// <summary>The calling key, its scopes and the player it acts for. Use it to check a key works.</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(IntegrationCallerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<IntegrationCallerDto>> Me(CancellationToken cancellationToken)
    {
        if (!IntegrationApiKeyAuthentication.TryGetCaller(User, out var keyId, out _))
            return Unauthorized();
        var caller = await _keys.DescribeAsync(keyId, cancellationToken);
        return caller is null ? Unauthorized() : Ok(caller);
    }

    /// <summary>
    /// Games the key's owner hosts that start in [<paramref name="from"/>, <paramref name="to"/>),
    /// earliest first, with roster totals. <paramref name="from"/> defaults to now and
    /// <paramref name="to"/> to 30 days after <paramref name="from"/>; the window may span at most
    /// 92 days. Times are ISO 8601 and read as UTC unless they carry an offset.
    /// </summary>
    /// <param name="from">Window start (inclusive).</param>
    /// <param name="to">Window end (exclusive).</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Items per page, 1 to 100 (default 50).</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpGet("occurrences")]
    [ProducesResponseType(typeof(PaginatedResult<IntegrationOccurrenceDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PaginatedResult<IntegrationOccurrenceDto>>> ListOccurrences(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (!IntegrationApiKeyAuthentication.TryGetCaller(User, out _, out var playerId))
            return Unauthorized();

        var fromUtc = (from ?? _timeProvider.GetUtcNow()).UtcDateTime;
        var toUtc = to?.UtcDateTime ?? fromUtc + DefaultWindow;
        if (page < 1)
            ModelState.AddModelError(nameof(page), "page must be 1 or more.");
        if (pageSize is < 1 or > MaxPageSize)
            ModelState.AddModelError(nameof(pageSize), $"pageSize must be between 1 and {MaxPageSize}.");
        if (toUtc <= fromUtc)
            ModelState.AddModelError(nameof(to), "to must be after from.");
        else if (toUtc - fromUtc > IIntegrationRosterService.MaxWindow)
            ModelState.AddModelError(nameof(to), $"The window may span at most {IIntegrationRosterService.MaxWindow.TotalDays:0} days.");
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        return Ok(await _rosters.ListHostedOccurrencesAsync(playerId, fromUtc, toUtc, page, pageSize, cancellationToken));
    }

    /// <summary>
    /// The roster of one hosted game: the game, its venue, requirements with open spots, and
    /// the players holding a spot. 404 when the game does not exist or another player hosts it.
    /// </summary>
    [HttpGet("occurrences/{occurrenceId:guid}/roster")]
    [ProducesResponseType(typeof(IntegrationRosterDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<IntegrationRosterDto>> GetRoster(Guid occurrenceId, CancellationToken cancellationToken)
    {
        if (!IntegrationApiKeyAuthentication.TryGetCaller(User, out _, out var playerId))
            return Unauthorized();
        var roster = await _rosters.GetRosterAsync(playerId, occurrenceId, cancellationToken);
        return roster is null ? NotFound() : Ok(roster);
    }
}
