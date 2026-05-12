using Microsoft.AspNetCore.Mvc;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class JoinRequestsController : ControllerBase
{
    private readonly IJoinRequestService _joinRequestService;
    private readonly ICurrentUserContext _currentUser;
    private readonly ILogger<JoinRequestsController> _logger;

    public JoinRequestsController(IJoinRequestService joinRequestService, ICurrentUserContext currentUser, ILogger<JoinRequestsController> logger)
    {
        _joinRequestService = joinRequestService;
        _currentUser = currentUser;
        _logger = logger;
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(JoinRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<JoinRequestDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var joinRequest = await _joinRequestService.GetByIdAsync(id, cancellationToken);
        if (joinRequest == null)
        {
            _logger.LogInformation("Join request with ID {JoinRequestId} not found", id);
            return NotFound();
        }

        return Ok(joinRequest);
    }

    [HttpGet("teams/{teamId}")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByTeamId(
        Guid teamId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] RequestStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var result = await _joinRequestService.GetByTeamIdAsync(teamId, page, pageSize, status, cancellationToken);
        return Ok(result);
    }

    [HttpGet("players/{playerId}")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByPlayerId(
        Guid playerId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] RequestStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var result = await _joinRequestService.GetByPlayerIdAsync(playerId, page, pageSize, status, cancellationToken);
        return Ok(result);
    }

    [HttpPost]
    [ProducesResponseType(typeof(JoinRequestDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<JoinRequestDto>> Create(
        [FromBody] CreateJoinRequestDto createJoinRequestDto,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var joinRequest = await _joinRequestService.CreateAsync(createJoinRequestDto, _currentUser.UserId, cancellationToken);
        _logger.LogInformation("Created join request {JoinRequestId} for team {TeamId}", joinRequest.Id, joinRequest.TeamId);
        return CreatedAtAction(nameof(GetById), new { id = joinRequest.Id }, joinRequest);
    }

    [HttpPut("{id}/process")]
    [ProducesResponseType(typeof(JoinRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<JoinRequestDto>> Process(
        Guid id,
        [FromBody] ProcessJoinRequestDto processJoinRequestDto,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var joinRequest = await _joinRequestService.ProcessAsync(id, processJoinRequestDto, _currentUser.UserId, cancellationToken);
        if (joinRequest == null)
        {
            _logger.LogInformation("Join request with ID {JoinRequestId} not found for processing", id);
            return NotFound();
        }

        _logger.LogInformation("Processed join request {JoinRequestId} with status {Status}", id, processJoinRequestDto.Status);
        return Ok(joinRequest);
    }
}
