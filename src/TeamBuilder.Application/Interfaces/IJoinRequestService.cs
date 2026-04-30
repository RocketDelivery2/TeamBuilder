using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Application.Interfaces;

public interface IJoinRequestService
{
    Task<JoinRequestDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PaginatedResult<JoinRequestDto>> GetByTeamIdAsync(Guid teamId, int page, int pageSize, RequestStatus? status = null, CancellationToken cancellationToken = default);
    Task<PaginatedResult<JoinRequestDto>> GetByPlayerIdAsync(Guid playerId, int page, int pageSize, RequestStatus? status = null, CancellationToken cancellationToken = default);
    Task<JoinRequestDto> CreateAsync(CreateJoinRequestDto createJoinRequestDto, Guid playerId, CancellationToken cancellationToken = default);
    Task<JoinRequestDto?> ProcessAsync(Guid id, ProcessJoinRequestDto processJoinRequestDto, Guid processedByUserId, CancellationToken cancellationToken = default);
}
