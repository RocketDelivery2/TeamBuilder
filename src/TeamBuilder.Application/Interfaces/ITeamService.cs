using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Application.Interfaces;

public interface ITeamService
{
    Task<TeamDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PaginatedResult<TeamDto>> GetAllAsync(int page, int pageSize, string? category = null, string? region = null, TeamStatus? status = null, CancellationToken cancellationToken = default);
    Task<TeamDto> CreateAsync(CreateTeamDto createTeamDto, Guid ownerId, CancellationToken cancellationToken = default);
    Task<TeamDto?> UpdateAsync(Guid id, UpdateTeamDto updateTeamDto, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
