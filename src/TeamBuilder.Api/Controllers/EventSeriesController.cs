using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TeamBuilder.Api.Auth;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Api.Controllers;

/// <summary>
/// Recurring event series. Creating a series materializes its first 21 local days of
/// occurrences; DELETE is a logical cancellation. There is no series editing yet.
/// </summary>
[ApiController]
[Route("api/v1/event-series")]
public class EventSeriesController : ControllerBase
{
    private readonly IEventSeriesService _eventSeriesService;
    private readonly ITeamService _teamService;
    private readonly ICurrentPlayerResolver _currentPlayerResolver;
    private readonly ILogger<EventSeriesController> _logger;

    public EventSeriesController(
        IEventSeriesService eventSeriesService,
        ITeamService teamService,
        ICurrentPlayerResolver currentPlayerResolver,
        ILogger<EventSeriesController> logger)
    {
        _eventSeriesService = eventSeriesService;
        _teamService = teamService;
        _currentPlayerResolver = currentPlayerResolver;
        _logger = logger;
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(EventSeriesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EventSeriesDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var series = await _eventSeriesService.GetByIdAsync(id, cancellationToken);
        if (series == null)
        {
            _logger.LogInformation("Event series with ID {SeriesId} not found", id);
            return NotFound();
        }

        return Ok(series);
    }

    [HttpGet("{id}/occurrences")]
    [ProducesResponseType(typeof(PaginatedResult<EventDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOccurrences(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var result = await _eventSeriesService.GetOccurrencesAsync(id, page, pageSize, cancellationToken);
        if (result == null)
        {
            _logger.LogInformation("Event series with ID {SeriesId} not found for occurrence list", id);
            return NotFound();
        }

        return Ok(result);
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(EventSeriesDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EventSeriesDto>> Create(
        [FromBody] CreateEventSeriesDto createDto,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var playerId = await _currentPlayerResolver.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        // Same contract as team-associated event creation (EventsController.Create).
        if (createDto.TeamId is { } teamId)
        {
            var team = await _teamService.GetByIdAsync(teamId, cancellationToken);
            if (team == null)
            {
                _logger.LogInformation("Team with ID {TeamId} not found for event series creation", teamId);
                return NotFound();
            }

            if (team.OwnerId != playerId.Value)
            {
                _logger.LogInformation("Player {PlayerId} is not the owner of team {TeamId}", playerId.Value, teamId);
                return Forbid();
            }

            if (team.LifecycleStatus != TeamLifecycleStatus.Active)
            {
                _logger.LogInformation("Team {TeamId} has lifecycle status {LifecycleStatus}; cannot create event series", teamId, team.LifecycleStatus);
                return Conflict(new { message = "Event series cannot be created for an inactive or disbanded team." });
            }
        }

        EventSeriesDto series;
        try
        {
            series = await _eventSeriesService.CreateAsync(createDto, playerId.Value, cancellationToken);
        }
        catch (VenueNotFoundException ex)
        {
            _logger.LogInformation("Venue with ID {VenueId} not found for event series creation", ex.VenueId);
            return NotFound();
        }
        catch (VenueAttachForbiddenException ex)
        {
            _logger.LogInformation("Player {PlayerId} may not use private venue {VenueId}", playerId.Value, ex.VenueId);
            return Forbid();
        }

        _logger.LogInformation("Created event series {SeriesId}", series.Id);
        return CreatedAtAction(nameof(GetById), new { id = series.Id }, series);
    }

    /// <summary>
    /// Logically cancels the series: the series row stays (status Cancelled) and its future,
    /// non-detached occurrences become Cancelled. Past and detached occurrences are untouched.
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        var playerId = await _currentPlayerResolver.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        var existing = await _eventSeriesService.GetByIdAsync(id, cancellationToken);
        if (existing == null)
        {
            _logger.LogInformation("Event series with ID {SeriesId} not found for cancellation", id);
            return NotFound();
        }

        if (existing.HostId == null)
        {
            _logger.LogInformation("Event series {SeriesId} has no host; cannot cancel orphaned series", id);
            return Conflict(new { message = "This event series has no host and cannot be cancelled. Contact an administrator." });
        }

        if (existing.HostId != playerId.Value)
        {
            _logger.LogInformation("Player {PlayerId} is not the host of event series {SeriesId}", playerId.Value, id);
            return Forbid();
        }

        // Concurrent or repeated cancellation surfaces as InvalidOperationException -> 409.
        if (!await _eventSeriesService.CancelAsync(id, cancellationToken))
            return NotFound();

        _logger.LogInformation("Cancelled event series {SeriesId}", id);
        return NoContent();
    }
}
