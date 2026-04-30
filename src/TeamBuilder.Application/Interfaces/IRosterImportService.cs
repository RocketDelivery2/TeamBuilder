using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;

namespace TeamBuilder.Application.Interfaces;

public interface IRosterImportService
{
    Task<RosterImportDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PaginatedResult<RosterImportDto>> GetAllAsync(int page, int pageSize, bool? isProcessed = null, CancellationToken cancellationToken = default);
    Task<RosterImportDto> CreateAsync(CreateRosterImportDto createRosterImportDto, Guid importedByUserId, CancellationToken cancellationToken = default);
    Task<RosterImportDto?> ProcessAsync(Guid id, Guid processedByUserId, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
