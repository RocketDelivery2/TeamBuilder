using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Infrastructure.Services;

public class RosterImportService : IRosterImportService
{
    private readonly TeamBuilderDbContext _context;

    public RosterImportService(TeamBuilderDbContext context)
    {
        _context = context;
    }

    public async Task<RosterImportDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var rosterImport = await _context.RosterImports
            .AsNoTracking()
            .FirstOrDefaultAsync(ri => ri.Id == id, cancellationToken);

        return rosterImport == null ? null : MapToDto(rosterImport);
    }

    public async Task<PaginatedResult<RosterImportSummaryDto>> GetByImporterAsync(
        Guid importerPlayerId,
        int page,
        int pageSize,
        bool? isProcessed = null,
        CancellationToken cancellationToken = default)
    {
        // Scope to the importer in the database before counting and paging.
        var query = _context.RosterImports
            .AsNoTracking()
            .Where(ri => ri.ImportedByUserId == importerPlayerId);

        if (isProcessed.HasValue)
            query = query.Where(ri => ri.IsProcessed == isProcessed.Value);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(ri => ri.CreatedAtUtc)
            .ThenBy(ri => ri.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(ri => new RosterImportSummaryDto
            {
                Id = ri.Id,
                SourceName = ri.SourceName,
                SourceType = ri.SourceType,
                IsProcessed = ri.IsProcessed,
                CreatedAtUtc = ri.CreatedAtUtc,
                UpdatedAtUtc = ri.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return new PaginatedResult<RosterImportSummaryDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<RosterImportDto> CreateAsync(
        CreateRosterImportDto createRosterImportDto, 
        Guid importedByUserId, 
        CancellationToken cancellationToken = default)
    {
        var rosterImport = new RosterImport
        {
            Id = Guid.NewGuid(),
            SourceName = createRosterImportDto.SourceName,
            SourceType = createRosterImportDto.SourceType,
            RawData = createRosterImportDto.RawData,
            IsProcessed = false,
            ImportedByUserId = importedByUserId
        };

        _context.RosterImports.Add(rosterImport);
        await _context.SaveChangesAsync(cancellationToken);

        return MapToDto(rosterImport);
    }

    public async Task<RosterImportDto?> ProcessAsync(
        Guid id, 
        Guid processedByUserId, 
        CancellationToken cancellationToken = default)
    {
        var rosterImport = await _context.RosterImports
            .FirstOrDefaultAsync(ri => ri.Id == id, cancellationToken);

        if (rosterImport == null) return null;

        if (rosterImport.IsProcessed)
            throw new InvalidOperationException("This roster import has already been processed.");

        // Parse the roster data and create roster entries
        var processedCount = await ParseAndCreateRosterEntries(rosterImport, cancellationToken);

        rosterImport.IsProcessed = true;
        rosterImport.ProcessedAtUtc = DateTime.UtcNow;
        rosterImport.ProcessingNotes = $"Successfully processed {processedCount} roster entries.";

        await _context.SaveChangesAsync(cancellationToken);

        return MapToDto(rosterImport);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var rosterImport = await _context.RosterImports
            .FirstOrDefaultAsync(ri => ri.Id == id, cancellationToken);

        if (rosterImport == null) return false;

        _context.RosterImports.Remove(rosterImport);
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    private async Task<int> ParseAndCreateRosterEntries(RosterImport rosterImport, CancellationToken cancellationToken)
    {
        // Simple CSV parsing: assumes format "PlayerName,Role,Notes"
        // This creates player records if they don't exist
        // In a real implementation, you might want to associate with specific events
        var lines = rosterImport.RawData.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var entriesCreated = 0;

        foreach (var line in lines.Skip(1)) // Skip header
        {
            var parts = line.Split(',');
            if (parts.Length < 1) continue;

            var playerName = parts[0].Trim();

            // Try to find existing player by username
            var player = await _context.Players
                .FirstOrDefaultAsync(p => p.Username == playerName, cancellationToken);

            if (player == null)
            {
                // Create new player if doesn't exist
                player = new Player
                {
                    Id = Guid.NewGuid(),
                    Username = playerName,
                    DisplayName = playerName
                };
                _context.Players.Add(player);
                entriesCreated++;
            }
        }

        if (entriesCreated > 0)
        {
            await _context.SaveChangesAsync(cancellationToken);
        }

        return entriesCreated;
    }

    private static RosterImportDto MapToDto(RosterImport rosterImport)
    {
        return new RosterImportDto
        {
            Id = rosterImport.Id,
            SourceName = rosterImport.SourceName,
            SourceType = rosterImport.SourceType,
            RawData = rosterImport.RawData,
            IsProcessed = rosterImport.IsProcessed,
            ProcessedAtUtc = rosterImport.ProcessedAtUtc,
            ProcessingNotes = rosterImport.ProcessingNotes,
            ImportedByUserId = rosterImport.ImportedByUserId,
            CreatedAtUtc = rosterImport.CreatedAtUtc
        };
    }
}
