using Microsoft.AspNetCore.Mvc;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;

namespace TeamBuilder.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class RosterImportsController : ControllerBase
{
    private readonly IRosterImportService _rosterImportService;
    private readonly ILogger<RosterImportsController> _logger;

    public RosterImportsController(IRosterImportService rosterImportService, ILogger<RosterImportsController> logger)
    {
        _rosterImportService = rosterImportService;
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
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
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
    [ProducesResponseType(typeof(RosterImportDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RosterImportDto>> Create(
        [FromBody] CreateRosterImportDto createRosterImportDto,
        [FromHeader(Name = "X-User-Id")] Guid? userId = null,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var importedByUserId = userId ?? Guid.Empty;

        try
        {
            var rosterImport = await _rosterImportService.CreateAsync(createRosterImportDto, importedByUserId, cancellationToken);
            _logger.LogInformation("Created roster import {RosterImportId} from source {SourceName}", rosterImport.Id, rosterImport.SourceName);
            return CreatedAtAction(nameof(GetById), new { id = rosterImport.Id }, rosterImport);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating roster import");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{id}/process")]
    [ProducesResponseType(typeof(RosterImportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RosterImportDto>> Process(
        Guid id,
        [FromHeader(Name = "X-User-Id")] Guid? userId = null,
        CancellationToken cancellationToken = default)
    {
        var processedByUserId = userId ?? Guid.Empty;

        try
        {
            var rosterImport = await _rosterImportService.ProcessAsync(id, processedByUserId, cancellationToken);
            if (rosterImport == null)
            {
                _logger.LogInformation("Roster import with ID {RosterImportId} not found for processing", id);
                return NotFound();
            }

            _logger.LogInformation("Processed roster import {RosterImportId}", id);
            return Ok(rosterImport);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to process roster import {RosterImportId}", id);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing roster import {RosterImportId}", id);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
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
