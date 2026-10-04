using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TeamBuilder.Api.Auth;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class EventsController : ControllerBase
{
    private readonly IEventService _eventService;
    private readonly ITeamService _teamService;
    private readonly ICurrentPlayerResolver _currentPlayerResolver;
    private readonly ILogger<EventsController> _logger;

    public EventsController(
        IEventService eventService,
        ITeamService teamService,
        ICurrentPlayerResolver currentPlayerResolver,
        ILogger<EventsController> logger)
    {
        _eventService = eventService;
        _teamService = teamService;
        _currentPlayerResolver = currentPlayerResolver;
        _logger = logger;
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(EventDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EventDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var teamEvent = await _eventService.GetByIdAsync(id, cancellationToken);
        if (teamEvent == null)
        {
            _logger.LogInformation("Event with ID {EventId} not found", id);
            return NotFound();
        }

        return Ok(teamEvent);
    }

    [HttpGet]
    [ProducesResponseType(typeof(PaginatedResult<EventDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? category = null,
        [FromQuery] string? region = null,
        [FromQuery] EventStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var result = await _eventService.GetAllAsync(page, pageSize, category, region, status, cancellationToken);
        return Ok(result);
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(EventDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EventDto>> Create(
        [FromBody] CreateEventDto createEventDto,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var playerId = await _currentPlayerResolver.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        if (createEventDto.TeamId is { } teamId)
        {
            var team = await _teamService.GetByIdAsync(teamId, cancellationToken);
            if (team == null)
            {
                _logger.LogInformation("Team with ID {TeamId} not found for event creation", teamId);
                return NotFound();
            }

            if (team.OwnerId != playerId.Value)
            {
                _logger.LogInformation("Player {PlayerId} is not the owner of team {TeamId}", playerId.Value, teamId);
                return Forbid();
            }

            // Only the administrative lifecycle matters; recruitment policy and physical
            // capacity never affect event scheduling.
            if (team.LifecycleStatus != TeamLifecycleStatus.Active)
            {
                _logger.LogInformation("Team {TeamId} has lifecycle status {LifecycleStatus}; cannot create event", teamId, team.LifecycleStatus);
                return Conflict(new { message = "Events cannot be created for an inactive or disbanded team." });
            }
        }

        var teamEvent = await _eventService.CreateAsync(createEventDto, playerId.Value, cancellationToken);
        var safeEventName = SanitizeForLog(teamEvent.Name);
        _logger.LogInformation("Created event {EventId} with name {EventName}", teamEvent.Id, safeEventName);
        return CreatedAtAction(nameof(GetById), new { id = teamEvent.Id }, teamEvent);
    }

    [HttpPut("{id}")]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(EventDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EventDto>> Update(
        Guid id,
        [FromBody] UpdateEventDto updateEventDto,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var playerId = await _currentPlayerResolver.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        var existing = await _eventService.GetByIdAsync(id, cancellationToken);
        if (existing == null)
        {
            _logger.LogInformation("Event with ID {EventId} not found for update", id);
            return NotFound();
        }

        if (existing.HostId == null)
        {
            _logger.LogInformation("Event {EventId} has no host; cannot update orphaned event", id);
            return Conflict(new { message = "This event has no host and cannot be updated. Contact an administrator." });
        }

        if (existing.HostId != playerId.Value)
        {
            _logger.LogInformation("Player {PlayerId} is not the host of event {EventId}", playerId.Value, id);
            return Forbid();
        }

        var teamEvent = await _eventService.UpdateAsync(id, updateEventDto, cancellationToken);
        _logger.LogInformation("Updated event {EventId}", id);
        return Ok(teamEvent!);
    }

    [HttpDelete("{id}")]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var playerId = await _currentPlayerResolver.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        var existing = await _eventService.GetByIdAsync(id, cancellationToken);
        if (existing == null)
        {
            _logger.LogInformation("Event with ID {EventId} not found for deletion", id);
            return NotFound();
        }

        if (existing.HostId == null)
        {
            _logger.LogInformation("Event {EventId} has no host; cannot delete orphaned event", id);
            return Conflict(new { message = "This event has no host and cannot be deleted. Contact an administrator." });
        }

        if (existing.HostId != playerId.Value)
        {
            _logger.LogInformation("Player {PlayerId} is not the host of event {EventId}", playerId.Value, id);
            return Forbid();
        }

        await _eventService.DeleteAsync(id, cancellationToken);
        _logger.LogInformation("Deleted event {EventId}", id);
        return NoContent();
    }

    private static string SanitizeForLog(string? value)
    {
        return string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
