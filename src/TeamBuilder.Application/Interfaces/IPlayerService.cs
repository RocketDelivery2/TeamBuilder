using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;

namespace TeamBuilder.Application.Interfaces;

public interface IPlayerService
{
    Task<PlayerDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PlayerDto?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default);
    Task<PaginatedResult<PlayerDto>> GetAllAsync(int page, int pageSize, string? region = null, CancellationToken cancellationToken = default);
    Task<PlayerDto> CreateAsync(CreatePlayerDto createPlayerDto, CancellationToken cancellationToken = default);
    Task<PlayerDto?> UpdateAsync(Guid id, UpdatePlayerDto updatePlayerDto, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
