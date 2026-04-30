using Microsoft.AspNetCore.Mvc;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class TeamsController : ControllerBase
{
    private readonly ITeamService _teamService;
    private readonly ILogger<TeamsController> _logger;

    public TeamsController(ITeamService teamService, ILogger<TeamsController> logger)
    {
        _teamService = teamService;
        _logger = logger;
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(TeamDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TeamDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var team = await _teamService.GetByIdAsync(id, cancellationToken);
        if (team == null)
        {
            _logger.LogInformation("Team with ID {TeamId} not found", id);
            return NotFound();
        }

        return Ok(team);
    }

    [HttpGet]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? category = null,
        [FromQuery] string? region = null,
        [FromQuery] TeamStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var result = await _teamService.GetAllAsync(page, pageSize, category, region, status, cancellationToken);
        return Ok(result);
    }

    [HttpPost]
    [ProducesResponseType(typeof(TeamDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TeamDto>> Create(
        [FromBody] CreateTeamDto createTeamDto,
        [FromHeader(Name = "X-User-Id")] Guid? userId = null,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var ownerId = userId ?? Guid.Empty;

        try
        {
            var team = await _teamService.CreateAsync(createTeamDto, ownerId, cancellationToken);
            _logger.LogInformation("Created team {TeamId} with name {TeamName}", team.Id, team.Name);
            return CreatedAtAction(nameof(GetById), new { id = team.Id }, team);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating team");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{id}")]
    [ProducesResponseType(typeof(TeamDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TeamDto>> Update(
        Guid id,
        [FromBody] UpdateTeamDto updateTeamDto,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var team = await _teamService.UpdateAsync(id, updateTeamDto, cancellationToken);
            if (team == null)
            {
                _logger.LogInformation("Team with ID {TeamId} not found for update", id);
                return NotFound();
            }

            _logger.LogInformation("Updated team {TeamId}", id);
            return Ok(team);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating team {TeamId}", id);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await _teamService.DeleteAsync(id, cancellationToken);
        if (!result)
        {
            _logger.LogInformation("Team with ID {TeamId} not found for deletion", id);
            return NotFound();
        }

        _logger.LogInformation("Deleted team {TeamId}", id);
        return NoContent();
    }

    [HttpPost("{teamId}/members/{playerId}/leave")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> LeaveTeam(
        Guid teamId,
        Guid playerId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _teamService.RemoveMemberAsync(teamId, playerId, cancellationToken);
            if (!result)
            {
                _logger.LogInformation("Team member not found: TeamId={TeamId}, PlayerId={PlayerId}", teamId, playerId);
                return NotFound();
            }

            _logger.LogInformation("Player {PlayerId} left team {TeamId}", playerId, teamId);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing player {PlayerId} from team {TeamId}", playerId, teamId);
            return BadRequest(new { error = ex.Message });
        }
    }
}
