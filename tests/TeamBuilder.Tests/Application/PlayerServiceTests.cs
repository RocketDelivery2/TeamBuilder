using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Application;

public class PlayerServiceTests : IDisposable
{
    private readonly TeamBuilderDbContext _context;
    private readonly PlayerService _playerService;

    public PlayerServiceTests()
    {
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TeamBuilderDbContext(options);
        _playerService = new PlayerService(_context);
    }

    [Fact]
    public async Task CreateAsync_ShouldCreatePlayer_Successfully()
    {
        // Arrange
        var createPlayerDto = new CreatePlayerDto
        {
            Username = "TestPlayer",
            Email = "test@example.com",
            DisplayName = "Test Player",
            Region = "NA"
        };

        // Act
        var result = await _playerService.CreateAsync(createPlayerDto);

        // Assert
        result.Should().NotBeNull();
        result.Username.Should().Be("TestPlayer");
        result.Email.Should().Be("test@example.com");

        var playerInDb = await _context.Players.FindAsync(result.Id);
        playerInDb.Should().NotBeNull();
        playerInDb!.Username.Should().Be("TestPlayer");
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowException_WhenUsernameExists()
    {
        // Arrange
        var existingPlayer = new Player
        {
            Id = Guid.NewGuid(),
            Username = "ExistingPlayer"
        };
        _context.Players.Add(existingPlayer);
        await _context.SaveChangesAsync();

        var createPlayerDto = new CreatePlayerDto
        {
            Username = "ExistingPlayer"
        };

        // Act
        Func<Task> act = async () => await _playerService.CreateAsync(createPlayerDto);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [Fact]
    public async Task GetByUsernameAsync_ShouldReturnPlayer_WhenExists()
    {
        // Arrange
        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = "TestUser"
        };
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        // Act
        var result = await _playerService.GetByUsernameAsync("TestUser");

        // Assert
        result.Should().NotBeNull();
        result!.Username.Should().Be("TestUser");
    }

    [Fact]
    public async Task UpdateAsync_ShouldUpdatePlayer_Successfully()
    {
        // Arrange
        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = "TestUser",
            Email = "old@example.com"
        };
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var updateDto = new UpdatePlayerDto
        {
            Email = "new@example.com",
            DisplayName = "New Display Name"
        };

        // Act
        var result = await _playerService.UpdateAsync(player.Id, updateDto);

        // Assert
        result.Should().NotBeNull();
        result!.Email.Should().Be("new@example.com");
        result.DisplayName.Should().Be("New Display Name");
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnPlayer_WhenExists()
    {
        // Arrange
        var player = new Player { Id = Guid.NewGuid(), Username = "LookupPlayer" };
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        // Act
        var result = await _playerService.GetByIdAsync(player.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(player.Id);
        result.Username.Should().Be("LookupPlayer");
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_WhenNotExists()
    {
        // Act
        var result = await _playerService.GetByIdAsync(Guid.NewGuid());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByUsernameAsync_ShouldReturnNull_WhenNotExists()
    {
        // Act
        var result = await _playerService.GetByUsernameAsync("ghost_user");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnPaginatedResults()
    {
        // Arrange
        for (int i = 0; i < 15; i++)
            _context.Players.Add(new Player { Id = Guid.NewGuid(), Username = $"Player{i}" });
        await _context.SaveChangesAsync();

        // Act
        var result = await _playerService.GetAllAsync(page: 1, pageSize: 10);

        // Assert
        result.Items.Should().HaveCount(10);
        result.TotalCount.Should().Be(15);
        result.TotalPages.Should().Be(2);
        result.HasNextPage.Should().BeTrue();
        result.HasPreviousPage.Should().BeFalse();
    }

    [Fact]
    public async Task GetAllAsync_ShouldFilterByRegion()
    {
        // Arrange
        _context.Players.Add(new Player { Id = Guid.NewGuid(), Username = "NaPlayer1", Region = "NA" });
        _context.Players.Add(new Player { Id = Guid.NewGuid(), Username = "EuPlayer1", Region = "EU" });
        _context.Players.Add(new Player { Id = Guid.NewGuid(), Username = "NaPlayer2", Region = "NA" });
        await _context.SaveChangesAsync();

        // Act
        var result = await _playerService.GetAllAsync(1, 10, region: "NA");

        // Assert
        result.TotalCount.Should().Be(2);
        result.Items.Should().OnlyContain(p => p.Region == "NA");
    }

    [Fact]
    public async Task DeleteAsync_ShouldDeletePlayer_Successfully()
    {
        // Arrange
        var player = new Player { Id = Guid.NewGuid(), Username = "DeleteMe" };
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        // Act
        var result = await _playerService.DeleteAsync(player.Id);

        // Assert
        result.Should().BeTrue();
        var deleted = await _context.Players.FindAsync(player.Id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ShouldReturnFalse_WhenNotExists()
    {
        // Act
        var result = await _playerService.DeleteAsync(Guid.NewGuid());

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAsync_ShouldReturnNull_WhenNotFound()
    {
        // Arrange
        var updateDto = new UpdatePlayerDto { Email = "ghost@example.com" };

        // Act
        var result = await _playerService.UpdateAsync(Guid.NewGuid(), updateDto);

        // Assert
        result.Should().BeNull();
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
