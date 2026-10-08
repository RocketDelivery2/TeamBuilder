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

    /// <summary>
    /// The game-page view of one occurrence: details, location, host, lifecycle, requirements
    /// with required/supply/open counts and readiness, the current participants (public fields
    /// only; ended rows are not listed), and the caller's relationship (isHost, my assignment).
    /// Public: anonymous or unlinked callers get the same view with no caller relationship.
    /// </summary>
    [HttpGet("{id}/detail")]
    [ProducesResponseType(typeof(OccurrenceDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OccurrenceDetailDto>> GetDetail(
        Guid id,
        [FromServices] IEventRosterService rosterService,
        CancellationToken cancellationToken)
    {
        var callerPlayerId = await _currentPlayerResolver.ResolvePlayerIdAsync(cancellationToken);
        var detail = await rosterService.GetOccurrenceDetailAsync(id, callerPlayerId, cancellationToken);
        if (detail == null)
        {
            _logger.LogInformation("Event with ID {EventId} not found for detail", id);
            return NotFound();
        }

        return Ok(detail);
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

        EventDto teamEvent;
        try
        {
            teamEvent = await _eventService.CreateAsync(createEventDto, playerId.Value, cancellationToken);
        }
        catch (VenueNotFoundException ex)
        {
            _logger.LogInformation("Venue with ID {VenueId} not found for event creation", ex.VenueId);
            return NotFound();
        }
        catch (VenueAttachForbiddenException ex)
        {
            _logger.LogInformation("Player {PlayerId} may not use private venue {VenueId}", playerId.Value, ex.VenueId);
            return Forbid();
        }

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

    /// <summary>
    /// Transfers stewardship of this occurrence (only this one, never its whole series) to
    /// another existing, identity-linked player. Current host only. Roster assignments,
    /// requirements, capacity, TeamId and status are unchanged; the old host loses host-only
    /// authority at commit and the new host gains it. Order: unlinked 403, missing occurrence
    /// 404, no host 409, non-host 403, unknown target 400, unlinked target 409
    /// HostTransferTargetNotLinked. Only a live occurrence (Planned, Open, InProgress) can change
    /// host: Completed, Cancelled or Archived gives 409 OccurrenceClosed (checked after the host
    /// check). A concurrent change that committed first gives 409 OccurrenceChanged.
    /// </summary>
    [HttpPost("{id}/host/transfer")]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(EventDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EventDto>> TransferHost(
        Guid id,
        [FromBody] TransferOccurrenceHostDto transferDto,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var playerId = await _currentPlayerResolver.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        try
        {
            var teamEvent = await _eventService.TransferHostAsync(id, playerId.Value, transferDto.NewHostPlayerId!.Value, cancellationToken);
            _logger.LogInformation("Transferred host of event {EventId} from {OldHostId} to {NewHostId}", id, playerId.Value, teamEvent.HostId);
            return Ok(teamEvent);
        }
        catch (EventOccurrenceNotFoundException)
        {
            return NotFound();
        }
        catch (OccurrenceHostForbiddenException)
        {
            _logger.LogInformation("Player {PlayerId} is not the host of event {EventId}", playerId.Value, id);
            return Forbid();
        }
    }

    private static string SanitizeForLog(string? value)
    {
        return string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
