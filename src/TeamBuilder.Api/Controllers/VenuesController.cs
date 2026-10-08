using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TeamBuilder.Api.Auth;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;

namespace TeamBuilder.Api.Controllers;

/// <summary>
/// Create and read venues. No update or delete: venues can be shared by other hosts' games, so
/// a host who needs a different place creates a new venue rather than moving a shared one.
/// </summary>
[ApiController]
[Route("api/v1/venues")]
public class VenuesController : ControllerBase
{
    private readonly IVenueService _venueService;
    private readonly ICurrentPlayerResolver _currentPlayerResolver;
    private readonly ILogger<VenuesController> _logger;

    public VenuesController(IVenueService venueService, ICurrentPlayerResolver currentPlayerResolver, ILogger<VenuesController> logger)
    {
        _venueService = venueService;
        _currentPlayerResolver = currentPlayerResolver;
        _logger = logger;
    }

    /// <summary>
    /// Creates a venue owned by the caller. A physical (Indoor/Outdoor) venue requires
    /// coordinates in range and an IANA time zone; a Virtual venue has no coordinates.
    /// Unlinked caller 403.
    /// </summary>
    [HttpPost]
    [Authorize(AuthenticationSchemes = ExternalIdentityAuthentication.SchemeName)]
    [ProducesResponseType(typeof(VenueDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<VenueDto>> Create([FromBody] CreateVenueDto createDto, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var playerId = await _currentPlayerResolver.ResolvePlayerIdAsync(cancellationToken);
        if (playerId == null)
            return Forbid();

        var venue = await _venueService.CreateAsync(createDto, playerId.Value, cancellationToken);
        // Coordinates and address are never logged.
        _logger.LogInformation("Player {PlayerId} created venue {VenueId}", playerId.Value, venue.Id);
        return CreatedAtAction(nameof(GetById), new { id = venue.Id }, venue);
    }

    /// <summary>
    /// One venue. Public: a Private venue's street address, postal code and coordinates are
    /// masked unless the caller created it.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(VenueDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VenueDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var callerPlayerId = await _currentPlayerResolver.ResolvePlayerIdAsync(cancellationToken);
        var venue = await _venueService.GetByIdAsync(id, callerPlayerId, cancellationToken);
        return venue is null ? NotFound() : Ok(venue);
    }
}
