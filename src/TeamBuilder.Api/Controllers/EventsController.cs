using Microsoft.AspNetCore.Mvc;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class EventsController : ControllerBase
{
    private readonly IEventService _eventService;
    private readonly ILogger<EventsController> _logger;

    public EventsController(IEventService eventService, ILogger<EventsController> logger)
    {
        _eventService = eventService;
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
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
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
    [ProducesResponseType(typeof(EventDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EventDto>> Create(
        [FromBody] CreateEventDto createEventDto,
        [FromHeader(Name = "X-User-Id")] Guid? userId = null,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var hostId = userId ?? Guid.Empty;

        try
        {
            var teamEvent = await _eventService.CreateAsync(createEventDto, hostId, cancellationToken);
            var safeEventName = SanitizeForLog(teamEvent.Name);
            _logger.LogInformation("Created event {EventId} with name {EventName}", teamEvent.Id, safeEventName);
            return CreatedAtAction(nameof(GetById), new { id = teamEvent.Id }, teamEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating event");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{id}")]
    [ProducesResponseType(typeof(EventDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EventDto>> Update(
        Guid id,
        [FromBody] UpdateEventDto updateEventDto,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var teamEvent = await _eventService.UpdateAsync(id, updateEventDto, cancellationToken);
            if (teamEvent == null)
            {
                _logger.LogInformation("Event with ID {EventId} not found for update", id);
                return NotFound();
            }

            _logger.LogInformation("Updated event {EventId}", id);
            return Ok(teamEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating event {EventId}", id);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await _eventService.DeleteAsync(id, cancellationToken);
        if (!result)
        {
            _logger.LogInformation("Event with ID {EventId} not found for deletion", id);
            return NotFound();
        }

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
