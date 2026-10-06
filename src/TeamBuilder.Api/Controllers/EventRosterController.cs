using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TeamBuilder.Api.Auth;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Api.Controllers;

/// <summary>
/// Roster requirements and live assignments of one event occurrence. Reads are public and
/// expose no email or identity data. Mutations are host-only: missing occurrence 404, orphaned
/// (no host) 409, non-host 403, then completed/cancelled/archived occurrence 409.
/// Event participation does not require team membership.
/// </summary>
[ApiController]
[Route("api/v1/events/{occurrenceId}/roster")]
public class EventRosterController : ControllerBase
{
    private readonly IEventRosterService _rosterService;
    private readonly IEventService _eventService;
    private readonly ICurrentPlayerResolver _currentPlayerResolver;
    private readonly ILogger<EventRosterController> _logger;

    public EventRosterController(
        IEventRosterService rosterService,
        IEventService eventService,
        ICurrentPlayerResolver currentPlayerResolver,
        ILogger<EventRosterController> logger)
    {
        _rosterService = rosterService;
        _eventService = eventService;
        _currentPlayerResolver = currentPlayerResolver;
        _logger = logger;
    }

    /// <summary>
    /// Readiness summary: total required/supply/open quantity, IsRosterReady, the requirements
    /// and every assignment (history included). Public.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(RosterSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RosterSummaryDto>> GetSummary(Guid occurrenceId, CancellationToken cancellationToken)
    {
        var summary = await _rosterService.GetSummaryAsync(occurrenceId, cancellationToken);
        if (summary == null)
        {
            _logger.LogInformation("Event with ID {EventId} not found for roster summary", occurrenceId);
            return NotFound();
        }

        return Ok(summary);
    }

    [HttpGet("requirements")]
    [ProducesResponseType(typeof(IReadOnlyList<RosterRequirementDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<RosterRequirementDto>>> GetRequirements(Guid occurrenceId, CancellationToken cancellationToken)
    {
        var requirements = await _rosterService.GetRequirementsAsync(occurrenceId, cancellationToken);
        if (requirements == null)
        {
            _logger.LogInformation("Event with ID {EventId} not found for roster requirements", occurrenceId);
            return NotFound();
        }

        return Ok(requirements);
    }

    [HttpPost("requirements")]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(RosterRequirementDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RosterRequirementDto>> CreateRequirement(
        Guid occurrenceId,
        [FromBody] CreateRosterRequirementDto createDto,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var denied = await AuthorizeHostMutationAsync(occurrenceId, cancellationToken);
        if (denied != null)
            return denied;

        try
        {
            var requirement = await _rosterService.CreateRequirementAsync(occurrenceId, createDto, cancellationToken);
            _logger.LogInformation("Created roster requirement {RequirementId} for event {EventId}", requirement.Id, occurrenceId);
            return CreatedAtAction(nameof(GetRequirements), new { occurrenceId }, requirement);
        }
        catch (EventOccurrenceNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("assignments")]
    [ProducesResponseType(typeof(PaginatedResult<RosterAssignmentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAssignments(
        Guid occurrenceId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 50;

        var result = await _rosterService.GetAssignmentsAsync(occurrenceId, page, pageSize, cancellationToken);
        if (result == null)
        {
            _logger.LogInformation("Event with ID {EventId} not found for roster assignments", occurrenceId);
            return NotFound();
        }

        return Ok(result);
    }

    /// <summary>
    /// Host assigns any existing player (team membership not required). Self-service
    /// reservation is a separate, later API.
    /// </summary>
    [HttpPost("assignments")]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(RosterAssignmentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RosterAssignmentDto>> CreateAssignment(
        Guid occurrenceId,
        [FromBody] CreateRosterAssignmentDto createDto,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var denied = await AuthorizeHostMutationAsync(occurrenceId, cancellationToken);
        if (denied != null)
            return denied;

        try
        {
            var assignment = await _rosterService.CreateAssignmentAsync(occurrenceId, createDto, RosterAssignmentSource.Host, cancellationToken);
            _logger.LogInformation("Created roster assignment {AssignmentId} for event {EventId}", assignment.Id, occurrenceId);
            return CreatedAtAction(nameof(GetAssignments), new { occurrenceId }, assignment);
        }
        catch (EventOccurrenceNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>Returns the denial result, or null when the caller is the occurrence's host and it accepts roster changes.</summary>
    private async Task<ActionResult?> AuthorizeHostMutationAsync(Guid occurrenceId, CancellationToken cancellationToken)
    {
        var playerId = await _currentPlayerResolver.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        var occurrence = await _eventService.GetByIdAsync(occurrenceId, cancellationToken);
        if (occurrence == null)
        {
            _logger.LogInformation("Event with ID {EventId} not found for roster change", occurrenceId);
            return NotFound();
        }

        if (occurrence.HostId == null)
        {
            _logger.LogInformation("Event {EventId} has no host; cannot change roster of orphaned event", occurrenceId);
            return Conflict(new { message = "This event has no host and its roster cannot be changed. Contact an administrator." });
        }

        if (occurrence.HostId != playerId.Value)
        {
            _logger.LogInformation("Player {PlayerId} is not the host of event {EventId}", playerId.Value, occurrenceId);
            return Forbid();
        }

        if (!RosterState.AcceptsNewRosterMutations(occurrence.Status))
        {
            _logger.LogInformation("Event {EventId} has status {Status}; roster is closed", occurrenceId, occurrence.Status);
            return Conflict(new { message = "Roster changes are not allowed for a completed, cancelled or archived event." });
        }

        return null;
    }
}
