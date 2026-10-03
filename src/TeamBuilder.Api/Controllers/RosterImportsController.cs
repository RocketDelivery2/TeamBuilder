using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TeamBuilder.Api.Auth;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;

namespace TeamBuilder.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class RosterImportsController : ControllerBase
{
    private readonly IRosterImportService _rosterImportService;
    private readonly ICurrentPlayerResolver _currentPlayerResolver;
    private readonly ILogger<RosterImportsController> _logger;

    public RosterImportsController(
        IRosterImportService rosterImportService,
        ICurrentPlayerResolver currentPlayerResolver,
        ILogger<RosterImportsController> logger)
    {
        _rosterImportService = rosterImportService;
        _currentPlayerResolver = currentPlayerResolver;
        _logger = logger;
    }

    [HttpGet("{id}")]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(RosterImportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RosterImportDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var playerId = await _currentPlayerResolver.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        var rosterImport = await _rosterImportService.GetByIdAsync(id, cancellationToken);
        if (rosterImport == null)
        {
            _logger.LogInformation("Roster import with ID {RosterImportId} not found", id);
            return NotFound();
        }

        // Importer-only: the detail includes RawData. Orphaned imports (no importer) are readable by no one.
        if (rosterImport.ImportedByUserId == null || rosterImport.ImportedByUserId != playerId.Value)
        {
            _logger.LogInformation("Player {PlayerId} is not the importer of roster import {RosterImportId}", playerId.Value, id);
            return Forbid();
        }

        return Ok(rosterImport);
    }

    [HttpGet]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(PaginatedResult<RosterImportSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool? isProcessed = null,
        CancellationToken cancellationToken = default)
    {
        var playerId = await _currentPlayerResolver.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var result = await _rosterImportService.GetByImporterAsync(playerId.Value, page, pageSize, isProcessed, cancellationToken);
        return Ok(result);
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(RosterImportDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<RosterImportDto>> Create(
        [FromBody] CreateRosterImportDto createRosterImportDto,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var playerId = await _currentPlayerResolver.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        var rosterImport = await _rosterImportService.CreateAsync(createRosterImportDto, playerId.Value, cancellationToken);
        var safeSourceName = (rosterImport.SourceName ?? string.Empty)
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty);
        _logger.LogInformation("Created roster import {RosterImportId} from source {SourceName}", rosterImport.Id, safeSourceName);
        return CreatedAtAction(nameof(GetById), new { id = rosterImport.Id }, rosterImport);
    }

    [HttpPut("{id}/process")]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(RosterImportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RosterImportDto>> Process(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var playerId = await _currentPlayerResolver.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        var existing = await _rosterImportService.GetByIdAsync(id, cancellationToken);
        if (existing == null)
        {
            _logger.LogInformation("Roster import with ID {RosterImportId} not found for processing", id);
            return NotFound();
        }

        if (existing.ImportedByUserId == null)
        {
            _logger.LogInformation("Roster import {RosterImportId} has no importer; cannot process orphaned import", id);
            return Conflict(new { message = "This roster import has no owner and cannot be processed. Contact an administrator." });
        }

        if (existing.ImportedByUserId != playerId.Value)
        {
            _logger.LogInformation("Player {PlayerId} is not the importer of roster import {RosterImportId}", playerId.Value, id);
            return Forbid();
        }

        var rosterImport = await _rosterImportService.ProcessAsync(id, playerId.Value, cancellationToken);
        if (rosterImport == null)
        {
            _logger.LogInformation("Roster import with ID {RosterImportId} not found for processing", id);
            return NotFound();
        }

        _logger.LogInformation("Processed roster import {RosterImportId}", id);
        return Ok(rosterImport);
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

        var existing = await _rosterImportService.GetByIdAsync(id, cancellationToken);
        if (existing == null)
        {
            _logger.LogInformation("Roster import with ID {RosterImportId} not found for deletion", id);
            return NotFound();
        }

        if (existing.ImportedByUserId == null)
        {
            _logger.LogInformation("Roster import {RosterImportId} has no importer; cannot delete orphaned import", id);
            return Conflict(new { message = "This roster import has no owner and cannot be deleted. Contact an administrator." });
        }

        if (existing.ImportedByUserId != playerId.Value)
        {
            _logger.LogInformation("Player {PlayerId} is not the importer of roster import {RosterImportId}", playerId.Value, id);
            return Forbid();
        }

        await _rosterImportService.DeleteAsync(id, cancellationToken);
        _logger.LogInformation("Deleted roster import {RosterImportId}", id);
        return NoContent();
    }
}
