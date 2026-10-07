using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Application.Interfaces;

public interface IEventService
{
    Task<EventDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PaginatedResult<EventDto>> GetAllAsync(int page, int pageSize, string? category = null, string? region = null, EventStatus? status = null, CancellationToken cancellationToken = default);
    Task<EventDto> CreateAsync(CreateEventDto createEventDto, Guid hostId, CancellationToken cancellationToken = default);
    Task<EventDto?> UpdateAsync(Guid id, UpdateEventDto updateEventDto, CancellationToken cancellationToken = default);
    /// <summary>Deletes an occurrence without participation history; false when it does not exist.</summary>
    /// <exception cref="Exceptions.RosterConflictException">
    /// OccurrenceHasParticipationHistory (cancel it instead) or OccurrenceChanged.
    /// </exception>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Transfers stewardship of this one occurrence (never its series) from
    /// <paramref name="callerPlayerId"/>, who must be its current host, to an existing,
    /// identity-linked player. Changes nothing but the host (and, for a series occurrence,
    /// marks it detached). Transferring to oneself is a no-op.
    /// </summary>
    /// <exception cref="Exceptions.EventOccurrenceNotFoundException">The occurrence does not exist.</exception>
    /// <exception cref="Exceptions.OccurrenceHostForbiddenException">The caller is not the current host.</exception>
    /// <exception cref="ArgumentException">The target player does not exist.</exception>
    /// <exception cref="Exceptions.RosterConflictException">
    /// OccurrenceHasNoHost, HostTransferTargetNotLinked, or OccurrenceChanged (a concurrent change won).
    /// </exception>
    Task<EventDto> TransferHostAsync(Guid occurrenceId, Guid callerPlayerId, Guid newHostPlayerId, CancellationToken cancellationToken = default);
}
