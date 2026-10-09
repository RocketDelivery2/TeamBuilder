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
/// expose no email or identity data. Requirement creation, host assignment, host removal and the
/// day-of-game lifecycle (check-in, activate, no-show) are host-only: missing occurrence 404,
/// orphaned (no host) 409, non-host 403, then completed/cancelled/archived occurrence 409. Any linked player may self-claim open quantity
/// and leave their own assignment. Event participation does not require team membership, and
/// the host holds no roster spot unless they claim one.
/// </summary>
[ApiController]
[Route("api/v1/events/{occurrenceId}/roster")]
public class EventRosterController : ControllerBase
{
    private readonly IEventRosterService _rosterService;
    private readonly ICurrentPlayerResolver _currentPlayerResolver;
    private readonly ILogger<EventRosterController> _logger;

    public EventRosterController(
        IEventRosterService rosterService,
        ICurrentPlayerResolver currentPlayerResolver,
        ILogger<EventRosterController> logger)
    {
        _rosterService = rosterService;
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

    /// <summary>
    /// Host-only. Order: invalid body 400, unlinked 403, missing occurrence 404, no host 409
    /// OccurrenceHasNoHost, non-host 403, closed occurrence 409 OccurrenceClosed, duplicate role
    /// 409 DuplicateRequirementRole. Host authority is re-validated at commit time.
    /// </summary>
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

        var playerId = await _currentPlayerResolver.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        try
        {
            var requirement = await _rosterService.CreateRequirementAsync(occurrenceId, createDto, playerId.Value, cancellationToken);
            _logger.LogInformation("Created roster requirement {RequirementId} for event {EventId}", requirement.Id, occurrenceId);
            return CreatedAtAction(nameof(GetRequirements), new { occurrenceId }, requirement);
        }
        catch (EventOccurrenceNotFoundException)
        {
            return NotFound();
        }
        catch (OccurrenceHostForbiddenException)
        {
            _logger.LogInformation("Player {PlayerId} is not the host of event {EventId}", playerId.Value, occurrenceId);
            return Forbid();
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
    /// Host assigns any existing player (team membership not required) to a requirement of this
    /// occurrence. Shares the allocation path, and therefore the capacity guard, with player
    /// self-claim. Order: invalid body 400 (missing requirementId: 400 RequirementIdRequired),
    /// unlinked 403, missing occurrence 404, no host 409 OccurrenceHasNoHost, non-host 403,
    /// closed occurrence 409 OccurrenceClosed, unknown player 400, requirement of another
    /// occurrence 400 RequirementNotOnOccurrence, then the capacity 409s. Host authority is
    /// re-validated at commit time.
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

        var playerId = await _currentPlayerResolver.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        try
        {
            var assignment = await _rosterService.CreateAssignmentAsync(occurrenceId, createDto, playerId.Value, cancellationToken);
            _logger.LogInformation("Created roster assignment {AssignmentId} for event {EventId}", assignment.Id, occurrenceId);
            return CreatedAtAction(nameof(GetAssignments), new { occurrenceId }, assignment);
        }
        catch (EventOccurrenceNotFoundException)
        {
            return NotFound();
        }
        catch (OccurrenceHostForbiddenException)
        {
            _logger.LogInformation("Player {PlayerId} is not the host of event {EventId}", playerId.Value, occurrenceId);
            return Forbid();
        }
    }

    /// <summary>
    /// The caller claims one unit of open quantity on a requirement (a Confirmed assignment with
    /// source Player). Any linked player may claim, the host included; team membership is not
    /// required. Order: unlinked 403, missing occurrence 404, requirement not on this occurrence
    /// 404, closed occurrence 409, then 200 with the existing assignment when the caller already
    /// holds one on this requirement (safe retry), 409 AlreadyParticipating when it is on another,
    /// 409 RequirementFull / RosterChanged when capacity is lost.
    /// </summary>
    [HttpPost("claims")]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(RosterAssignmentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(RosterAssignmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RosterAssignmentDto>> Claim(
        Guid occurrenceId,
        [FromBody] ClaimRosterSpotDto claimDto,
        [FromServices] IInAppNotificationService notifications,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var playerId = await _currentPlayerResolver.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        try
        {
            var result = await _rosterService.ClaimAsync(occurrenceId, playerId.Value, claimDto, cancellationToken);
            if (!result.Created)
                return Ok(result.Assignment);

            _logger.LogInformation("Player {PlayerId} claimed roster assignment {AssignmentId} for event {EventId}", playerId.Value, result.Assignment.Id, occurrenceId);
            // Refill-funnel metrics only (notification opened -> claim, vacancy -> replacement); never affects the claim.
            await notifications.RecordClaimAttemptAsync(playerId.Value, occurrenceId, claimDto.RequirementId ?? Guid.Empty, succeeded: true, cancellationToken);
            return CreatedAtAction(nameof(GetAssignments), new { occurrenceId }, result.Assignment);
        }
        catch (RosterConflictException ex) when (ex.Code is RosterConflictCodes.RequirementFull or RosterConflictCodes.RosterChanged)
        {
            await notifications.RecordClaimAttemptAsync(playerId.Value, occurrenceId, claimDto.RequirementId ?? Guid.Empty, succeeded: false, cancellationToken);
            throw;
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
    /// The caller leaves their own live assignment. Reserved/Confirmed become Cancelled,
    /// CheckedIn/Active become Departed, with exit reason PlayerLeft; the row is kept and its
    /// spot reopens immediately. Order: unlinked 403, missing occurrence or assignment 404,
    /// not the caller's assignment 403, closed occurrence 409, already ended 409.
    /// </summary>
    [HttpPost("assignments/{assignmentId}/leave")]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(RosterAssignmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RosterAssignmentDto>> Leave(Guid occurrenceId, Guid assignmentId, CancellationToken cancellationToken)
    {
        var playerId = await _currentPlayerResolver.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        try
        {
            var assignment = await _rosterService.LeaveAsync(occurrenceId, assignmentId, playerId.Value, cancellationToken);
            _logger.LogInformation("Player {PlayerId} left roster assignment {AssignmentId} of event {EventId}", playerId.Value, assignmentId, occurrenceId);
            return Ok(assignment);
        }
        catch (EventOccurrenceNotFoundException)
        {
            return NotFound();
        }
        catch (RosterAssignmentNotFoundException)
        {
            return NotFound();
        }
        catch (RosterAssignmentForbiddenException)
        {
            return Forbid();
        }
    }

    /// <summary>
    /// The host ends a participant's live assignment (exit reason HostRemoved), with the same
    /// transitions and history retention as leave. Order: unlinked 403, missing occurrence 404,
    /// no host 409 OccurrenceHasNoHost, non-host 403, closed occurrence 409 OccurrenceClosed,
    /// missing assignment 404, already ended 409 AssignmentEnded.
    /// </summary>
    [HttpPost("assignments/{assignmentId}/remove")]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(RosterAssignmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<RosterAssignmentDto>> Remove(Guid occurrenceId, Guid assignmentId, CancellationToken cancellationToken) =>
        HostAssignmentActionAsync(
            occurrenceId,
            assignmentId,
            "removed",
            hostId => _rosterService.RemoveAsync(occurrenceId, assignmentId, hostId, cancellationToken),
            cancellationToken);

    /// <summary>
    /// Host check-in: Confirmed to CheckedIn (the player arrived; still holds the spot).
    /// Ordering as for remove, then 409 AssignmentEnded for an ended row or
    /// AssignmentTransitionInvalid from any other status.
    /// </summary>
    [HttpPost("assignments/{assignmentId}/check-in")]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(RosterAssignmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<RosterAssignmentDto>> CheckIn(Guid occurrenceId, Guid assignmentId, CancellationToken cancellationToken) =>
        TransitionAsync(occurrenceId, assignmentId, RosterAssignmentTransition.CheckIn, cancellationToken);

    /// <summary>Host activation: CheckedIn to Active (the player is in the game; still holds the spot).</summary>
    [HttpPost("assignments/{assignmentId}/activate")]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(RosterAssignmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<RosterAssignmentDto>> Activate(Guid occurrenceId, Guid assignmentId, CancellationToken cancellationToken) =>
        TransitionAsync(occurrenceId, assignmentId, RosterAssignmentTransition.Activate, cancellationToken);

    /// <summary>
    /// Host no-show: Confirmed or CheckedIn to NoShow (exit reason NoShow). The row is kept and
    /// its spot reopens in the same commit.
    /// </summary>
    [HttpPost("assignments/{assignmentId}/no-show")]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(RosterAssignmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<RosterAssignmentDto>> NoShow(Guid occurrenceId, Guid assignmentId, CancellationToken cancellationToken) =>
        TransitionAsync(occurrenceId, assignmentId, RosterAssignmentTransition.NoShow, cancellationToken);

    private Task<ActionResult<RosterAssignmentDto>> TransitionAsync(
        Guid occurrenceId,
        Guid assignmentId,
        RosterAssignmentTransition transition,
        CancellationToken cancellationToken) =>
        HostAssignmentActionAsync(
            occurrenceId,
            assignmentId,
            transition.ToString(),
            hostId => _rosterService.TransitionAsync(occurrenceId, assignmentId, hostId, transition, cancellationToken),
            cancellationToken);

    /// <summary>
    /// Host-only action on one assignment. Host authority is enforced by the service inside the
    /// same commit as the change, so it holds even against a concurrent host transfer.
    /// </summary>
    private async Task<ActionResult<RosterAssignmentDto>> HostAssignmentActionAsync(
        Guid occurrenceId,
        Guid assignmentId,
        string action,
        Func<Guid, Task<RosterAssignmentDto>> run,
        CancellationToken cancellationToken)
    {
        var playerId = await _currentPlayerResolver.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        try
        {
            var assignment = await run(playerId.Value);
            _logger.LogInformation("Host {PlayerId} applied {Action} to roster assignment {AssignmentId} of event {EventId}", playerId.Value, action, assignmentId, occurrenceId);
            return Ok(assignment);
        }
        catch (EventOccurrenceNotFoundException)
        {
            return NotFound();
        }
        catch (RosterAssignmentNotFoundException)
        {
            return NotFound();
        }
        catch (OccurrenceHostForbiddenException)
        {
            _logger.LogInformation("Player {PlayerId} is not the host of event {EventId}", playerId.Value, occurrenceId);
            return Forbid();
        }
    }
}
