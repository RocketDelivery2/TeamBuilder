using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TeamBuilder.Api.Auth;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class JoinRequestsController : ControllerBase
{
    private readonly IJoinRequestService _joinRequestService;
    private readonly ITeamService _teamService;
    private readonly ICurrentPlayerResolver _currentPlayer;
    private readonly ILogger<JoinRequestsController> _logger;

    public JoinRequestsController(
        IJoinRequestService joinRequestService,
        ITeamService teamService,
        ICurrentPlayerResolver currentPlayer,
        ILogger<JoinRequestsController> logger)
    {
        _joinRequestService = joinRequestService;
        _teamService = teamService;
        _currentPlayer = currentPlayer;
        _logger = logger;
    }

    [HttpGet("{id}")]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(JoinRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<JoinRequestDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var currentPlayerId = await _currentPlayer.ResolvePlayerIdAsync(cancellationToken);
        if (currentPlayerId is null)
        {
            _logger.LogInformation("Authenticated caller has no linked player; reading join request {JoinRequestId} forbidden", id);
            return Forbid();
        }

        var joinRequest = await _joinRequestService.GetByIdAsync(id, cancellationToken);
        if (joinRequest == null)
        {
            _logger.LogInformation("Join request with ID {JoinRequestId} not found", id);
            return NotFound();
        }

        // Only the applicant and the evaluating team's owner may read a join request.
        if (joinRequest.PlayerId != currentPlayerId.Value && joinRequest.TeamOwnerId != currentPlayerId.Value)
        {
            _logger.LogInformation("Player {PlayerId} is neither the applicant nor the team owner for join request {JoinRequestId}", currentPlayerId, id);
            return Forbid();
        }

        return Ok(joinRequest);
    }

    [HttpGet("teams/{teamId}")]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(PaginatedResult<JoinRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByTeamId(
        Guid teamId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] RequestStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        var currentPlayerId = await _currentPlayer.ResolvePlayerIdAsync(cancellationToken);
        if (currentPlayerId is null)
        {
            _logger.LogInformation("Authenticated caller has no linked player; reading join requests for team {TeamId} forbidden", teamId);
            return Forbid();
        }

        var team = await _teamService.GetByIdAsync(teamId, cancellationToken);
        if (team == null)
        {
            _logger.LogInformation("Team with ID {TeamId} not found", teamId);
            return NotFound();
        }

        // Only the team owner may read the team's join requests.
        if (team.OwnerId != currentPlayerId.Value)
        {
            _logger.LogInformation("Player {PlayerId} is not the owner of team {TeamId}", currentPlayerId, teamId);
            return Forbid();
        }

        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var result = await _joinRequestService.GetByTeamIdAsync(teamId, page, pageSize, status, cancellationToken);
        return Ok(result);
    }

    [HttpGet("players/{playerId}")]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(PaginatedResult<JoinRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetByPlayerId(
        Guid playerId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] RequestStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        var currentPlayerId = await _currentPlayer.ResolvePlayerIdAsync(cancellationToken);
        if (currentPlayerId is null)
        {
            _logger.LogInformation("Authenticated caller has no linked player; reading player join requests forbidden");
            return Forbid();
        }

        // Self-only: checked before querying so another player's history is never loaded.
        if (playerId != currentPlayerId.Value)
        {
            _logger.LogInformation("Player {PlayerId} may not read join requests of player {TargetPlayerId}", currentPlayerId, playerId);
            return Forbid();
        }

        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var result = await _joinRequestService.GetByPlayerIdAsync(playerId, page, pageSize, status, cancellationToken);
        return Ok(result);
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(JoinRequestDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<JoinRequestDto>> Create(
        [FromBody] CreateJoinRequestDto createJoinRequestDto,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var currentPlayerId = await _currentPlayer.ResolvePlayerIdAsync(cancellationToken);
        if (currentPlayerId is null)
        {
            _logger.LogInformation("Authenticated caller has no linked player; join request creation forbidden");
            return Forbid();
        }

        var joinRequest = await _joinRequestService.CreateAsync(createJoinRequestDto, currentPlayerId.Value, cancellationToken);
        _logger.LogInformation("Created join request {JoinRequestId} for team {TeamId}", joinRequest.Id, joinRequest.TeamId);
        return CreatedAtAction(nameof(GetById), new { id = joinRequest.Id }, joinRequest);
    }

    [HttpPut("{id}/process")]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(JoinRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<JoinRequestDto>> Process(
        Guid id,
        [FromBody] ProcessJoinRequestDto processJoinRequestDto,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var currentPlayerId = await _currentPlayer.ResolvePlayerIdAsync(cancellationToken);
        if (currentPlayerId is null)
        {
            _logger.LogInformation("Authenticated caller has no linked player; processing join request {JoinRequestId} forbidden", id);
            return Forbid();
        }

        JoinRequestDto? joinRequest;
        try
        {
            joinRequest = await _joinRequestService.ProcessAsync(id, processJoinRequestDto, currentPlayerId.Value, cancellationToken);
        }
        catch (JoinRequestProcessingForbiddenException)
        {
            _logger.LogInformation("Player {PlayerId} is not the owner of the team for join request {JoinRequestId}", currentPlayerId, id);
            return Forbid();
        }

        if (joinRequest == null)
        {
            _logger.LogInformation("Join request with ID {JoinRequestId} not found for processing", id);
            return NotFound();
        }

        _logger.LogInformation("Processed join request {JoinRequestId} with status {Status}", id, processJoinRequestDto.Status);
        return Ok(joinRequest);
    }
}
