using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Application;

public class RosterImportServiceTests : IDisposable
{
    private readonly TeamBuilderDbContext _context;
    private readonly RosterImportService _rosterImportService;

    public RosterImportServiceTests()
    {
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TeamBuilderDbContext(options);
        _rosterImportService = new RosterImportService(_context);
    }

    [Fact]
    public async Task CreateAsync_ShouldCreateRosterImport_Successfully()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var createDto = new CreateRosterImportDto
        {
            SourceName = "TestSource",
            SourceType = "CSV",
            RawData = "Name,Role\nPlayer1,Tank\nPlayer2,DPS"
        };

        // Act
        var result = await _rosterImportService.CreateAsync(createDto, userId);

        // Assert
        result.Should().NotBeNull();
        result.SourceName.Should().Be("TestSource");
        result.SourceType.Should().Be("CSV");
        result.IsProcessed.Should().BeFalse();
        result.ImportedByUserId.Should().Be(userId);

        var importInDb = await _context.RosterImports.FindAsync(result.Id);
        importInDb.Should().NotBeNull();
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnRosterImport_WhenExists()
    {
        // Arrange
        var rosterImport = new RosterImport
        {
            Id = Guid.NewGuid(),
            SourceName = "TestSource",
            SourceType = "CSV",
            RawData = "test data",
            IsProcessed = false
        };
        _context.RosterImports.Add(rosterImport);
        await _context.SaveChangesAsync();

        // Act
        var result = await _rosterImportService.GetByIdAsync(rosterImport.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(rosterImport.Id);
        result.SourceName.Should().Be("TestSource");
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_WhenNotExists()
    {
        // Act
        var result = await _rosterImportService.GetByIdAsync(Guid.NewGuid());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnPaginatedResults()
    {
        // Arrange
        for (int i = 0; i < 15; i++)
        {
            _context.RosterImports.Add(new RosterImport
            {
                Id = Guid.NewGuid(),
                SourceName = $"Source{i}",
                SourceType = "CSV",
                RawData = "test",
                IsProcessed = i % 2 == 0
            });
        }
        await _context.SaveChangesAsync();

        // Act
        var result = await _rosterImportService.GetAllAsync(1, 10);

        // Assert
        result.Items.Should().HaveCount(10);
        result.TotalCount.Should().Be(15);
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(10);
        result.TotalPages.Should().Be(2);
    }

    [Fact]
    public async Task GetAllAsync_ShouldFilterByProcessedStatus()
    {
        // Arrange
        for (int i = 0; i < 10; i++)
        {
            _context.RosterImports.Add(new RosterImport
            {
                Id = Guid.NewGuid(),
                SourceName = $"Source{i}",
                SourceType = "CSV",
                RawData = "test",
                IsProcessed = i < 5
            });
        }
        await _context.SaveChangesAsync();

        // Act
        var result = await _rosterImportService.GetAllAsync(1, 20, isProcessed: true);

        // Assert
        result.Items.Should().HaveCount(5);
        result.Items.Should().OnlyContain(ri => ri.IsProcessed);
    }

    [Fact]
    public async Task ProcessAsync_ShouldMarkAsProcessed_AndCreatePlayers()
    {
        // Arrange
        var rosterImport = new RosterImport
        {
            Id = Guid.NewGuid(),
            SourceName = "TestSource",
            SourceType = "CSV",
            RawData = "Name,Role\nNewPlayer1,Tank\nNewPlayer2,DPS",
            IsProcessed = false
        };
        _context.RosterImports.Add(rosterImport);
        await _context.SaveChangesAsync();

        // Act
        var result = await _rosterImportService.ProcessAsync(rosterImport.Id, Guid.NewGuid());

        // Assert
        result.Should().NotBeNull();
        result!.IsProcessed.Should().BeTrue();
        result.ProcessedAtUtc.Should().NotBeNull();
        result.ProcessingNotes.Should().Contain("2");

        var players = await _context.Players
            .Where(p => p.Username == "NewPlayer1" || p.Username == "NewPlayer2")
            .ToListAsync();
        players.Should().HaveCount(2);
    }

    [Fact]
    public async Task ProcessAsync_ShouldThrow_WhenAlreadyProcessed()
    {
        // Arrange
        var rosterImport = new RosterImport
        {
            Id = Guid.NewGuid(),
            SourceName = "TestSource",
            SourceType = "CSV",
            RawData = "test",
            IsProcessed = true
        };
        _context.RosterImports.Add(rosterImport);
        await _context.SaveChangesAsync();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _rosterImportService.ProcessAsync(rosterImport.Id, Guid.NewGuid()));
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveRosterImport_Successfully()
    {
        // Arrange
        var rosterImport = new RosterImport
        {
            Id = Guid.NewGuid(),
            SourceName = "TestSource",
            SourceType = "CSV",
            RawData = "test",
            IsProcessed = false
        };
        _context.RosterImports.Add(rosterImport);
        await _context.SaveChangesAsync();

        // Act
        var result = await _rosterImportService.DeleteAsync(rosterImport.Id);

        // Assert
        result.Should().BeTrue();
        var deletedImport = await _context.RosterImports.FindAsync(rosterImport.Id);
        deletedImport.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ShouldReturnFalse_WhenNotExists()
    {
        // Act
        var result = await _rosterImportService.DeleteAsync(Guid.NewGuid());

        // Assert
        result.Should().BeFalse();
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
