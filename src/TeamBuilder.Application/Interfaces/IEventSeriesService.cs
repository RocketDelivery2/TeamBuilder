using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;

namespace TeamBuilder.Application.Interfaces;

public interface IEventSeriesService
{
    Task<EventSeriesDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Occurrences of the series ordered by start; null when the series does not exist.</summary>
    Task<PaginatedResult<EventDto>?> GetOccurrencesAsync(Guid id, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the series and its initial 21-local-day window of occurrences atomically.
    /// Authorization (team ownership/lifecycle) is the caller's responsibility.
    /// </summary>
    /// <exception cref="Exceptions.VenueNotFoundException">The venue does not exist.</exception>
    /// <exception cref="ArgumentException">Time zone or start date is invalid.</exception>
    Task<EventSeriesDto> CreateAsync(CreateEventSeriesDto createDto, Guid hostId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Logically cancels the series and its future, non-detached occurrences in one save.
    /// Returns false when the series does not exist.
    /// </summary>
    /// <exception cref="InvalidOperationException">Already cancelled, or changed concurrently.</exception>
    Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken = default);
}
