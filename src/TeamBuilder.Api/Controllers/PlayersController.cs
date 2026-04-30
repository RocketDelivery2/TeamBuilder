using Microsoft.AspNetCore.Mvc;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;

namespace TeamBuilder.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class PlayersController : ControllerBase
{
    private readonly IPlayerService _playerService;
    private readonly ILogger<PlayersController> _logger;

    public PlayersController(IPlayerService playerService, ILogger<PlayersController> logger)
    {
        _playerService = playerService;
        _logger = logger;
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(PlayerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlayerDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var player = await _playerService.GetByIdAsync(id, cancellationToken);
        if (player == null)
        {
            _logger.LogInformation("Player with ID {PlayerId} not found", id);
            return NotFound();
        }

        return Ok(player);
    }

    [HttpGet("username/{username}")]
    [ProducesResponseType(typeof(PlayerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlayerDto>> GetByUsername(string username, CancellationToken cancellationToken)
    {
        var player = await _playerService.GetByUsernameAsync(username, cancellationToken);
        if (player == null)
        {
            _logger.LogInformation("Player with username {Username} not found", username);
            return NotFound();
        }

        return Ok(player);
    }

    [HttpGet]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? region = null,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var result = await _playerService.GetAllAsync(page, pageSize, region, cancellationToken);
        return Ok(result);
    }

    [HttpPost]
    [ProducesResponseType(typeof(PlayerDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PlayerDto>> Create(
        [FromBody] CreatePlayerDto createPlayerDto,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var player = await _playerService.CreateAsync(createPlayerDto, cancellationToken);
            _logger.LogInformation("Created player {PlayerId} with username {Username}", player.Id, player.Username);
            return CreatedAtAction(nameof(GetById), new { id = player.Id }, player);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to create player with username {Username}", createPlayerDto.Username);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating player");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{id}")]
    [ProducesResponseType(typeof(PlayerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PlayerDto>> Update(
        Guid id,
        [FromBody] UpdatePlayerDto updatePlayerDto,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var player = await _playerService.UpdateAsync(id, updatePlayerDto, cancellationToken);
            if (player == null)
            {
                _logger.LogInformation("Player with ID {PlayerId} not found for update", id);
                return NotFound();
            }

            _logger.LogInformation("Updated player {PlayerId}", id);
            return Ok(player);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating player {PlayerId}", id);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await _playerService.DeleteAsync(id, cancellationToken);
        if (!result)
        {
            _logger.LogInformation("Player with ID {PlayerId} not found for deletion", id);
            return NotFound();
        }

        _logger.LogInformation("Deleted player {PlayerId}", id);
        return NoContent();
    }
}
