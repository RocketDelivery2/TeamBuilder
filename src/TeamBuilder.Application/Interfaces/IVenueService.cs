using TeamBuilder.Application.DTOs;

namespace TeamBuilder.Application.Interfaces;

public interface IVenueService
{
    /// <summary>Creates a venue owned by <paramref name="creatorPlayerId"/>.</summary>
    Task<VenueDto> CreateAsync(CreateVenueDto createDto, Guid creatorPlayerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The venue as <paramref name="callerPlayerId"/> may see it: a Private venue is masked
    /// unless the caller created it. Null when it does not exist.
    /// </summary>
    Task<VenueDto?> GetByIdAsync(Guid id, Guid? callerPlayerId, CancellationToken cancellationToken = default);
}
