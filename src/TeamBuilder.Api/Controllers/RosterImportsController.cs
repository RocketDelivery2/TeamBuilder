using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;

namespace TeamBuilder.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class RosterImportsController : ControllerBase
{
    private readonly IRosterImportService _rosterImportService;
    private readonly ICurrentUserContext _currentUser;
    private readonly ILogger<RosterImportsController> _logger;

    public RosterImportsController(IRosterImportService rosterImportService, ICurrentUserContext currentUser, ILogger<RosterImportsController> logger)
    {
        _rosterImportService = rosterImportService;
        _currentUser = currentUser;
        _logger = logger;
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(RosterImportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RosterImportDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var rosterImport = await _rosterImportService.GetByIdAsync(id, cancellationToken);
        if (rosterImport == null)
        {
            _logger.LogInformation("Roster import with ID {RosterImportId} not found", id);
            return NotFound();
        }

        return Ok(rosterImport);
    }

    [HttpGet]
    [ProducesResponseType(typeof(PaginatedResult<RosterImportDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool? isProcessed = null,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var result = await _rosterImportService.GetAllAsync(page, pageSize, isProcessed, cancellationToken);
        return Ok(result);
    }

    [HttpPost]
    [Authorize]
    [ProducesResponseType(typeof(RosterImportDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<RosterImportDto>> Create(
        [FromBody] CreateRosterImportDto createRosterImportDto,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var rosterImport = await _rosterImportService.CreateAsync(createRosterImportDto, _currentUser.UserId, cancellationToken);
        var safeSourceName = (rosterImport.SourceName ?? string.Empty)
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty);
        _logger.LogInformation("Created roster import {RosterImportId} from source {SourceName}", rosterImport.Id, safeSourceName);
        return CreatedAtAction(nameof(GetById), new { id = rosterImport.Id }, rosterImport);
    }

    [HttpPut("{id}/process")]
    [Authorize]
    [ProducesResponseType(typeof(RosterImportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<RosterImportDto>> Process(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var rosterImport = await _rosterImportService.ProcessAsync(id, _currentUser.UserId, cancellationToken);
        if (rosterImport == null)
        {
            _logger.LogInformation("Roster import with ID {RosterImportId} not found for processing", id);
            return NotFound();
        }

        _logger.LogInformation("Processed roster import {RosterImportId}", id);
        return Ok(rosterImport);
    }

    [HttpDelete("{id}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await _rosterImportService.DeleteAsync(id, cancellationToken);
        if (!result)
        {
            _logger.LogInformation("Roster import with ID {RosterImportId} not found for deletion", id);
            return NotFound();
        }

        _logger.LogInformation("Deleted roster import {RosterImportId}", id);
        return NoContent();
    }
}
