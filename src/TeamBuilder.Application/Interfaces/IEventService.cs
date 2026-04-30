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
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
