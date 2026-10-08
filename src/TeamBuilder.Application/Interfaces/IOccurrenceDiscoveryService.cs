using TeamBuilder.Application.DTOs;

namespace TeamBuilder.Application.Interfaces;

public interface IOccurrenceDiscoveryService
{
    /// <summary>
    /// Live occurrences at physical venues within the radius, closest first, then by start and
    /// id, keyset-paged. <paramref name="viewerPlayerId"/> adds the caller's relationship and
    /// unmasks Private venues of games they host or play in.
    /// </summary>
    /// <exception cref="Exceptions.DiscoveryValidationException">
    /// The window is empty or too long, or the cursor is malformed or belongs to another search.
    /// </exception>
    Task<DiscoveredOccurrencePageDto> DiscoverAsync(
        DiscoverOccurrencesQuery query,
        Guid? viewerPlayerId,
        CancellationToken cancellationToken = default);
}
