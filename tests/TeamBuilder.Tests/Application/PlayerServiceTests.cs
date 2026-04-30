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
    public async Task GetByIdAsync_ShouldReturnPlayer_WhenExists()
    {
        // Arrange
        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = "LookupPlayer",
            Email = "lookup@example.com",
            Region = "EU"
        };
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        // Act
        var result = await _playerService.GetByIdAsync(player.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(player.Id);
        result.Username.Should().Be("LookupPlayer");
        result.Email.Should().Be("lookup@example.com");
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
        var result = await _playerService.GetByUsernameAsync("NonExistentUser");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnPaginatedResults()
    {
        // Arrange
        for (int i = 0; i < 12; i++)
        {
            _context.Players.Add(new Player { Id = Guid.NewGuid(), Username = $"Player{i}", Region = "NA" });
        }
        await _context.SaveChangesAsync();

        // Act
        var result = await _playerService.GetAllAsync(page: 1, pageSize: 10);

        // Assert
        result.Should().NotBeNull();
        result.Items.Should().HaveCount(10);
        result.TotalCount.Should().Be(12);
        result.TotalPages.Should().Be(2);
        result.HasNextPage.Should().BeTrue();
        result.HasPreviousPage.Should().BeFalse();
    }

    [Fact]
    public async Task GetAllAsync_ShouldFilterByRegion()
    {
        // Arrange
        _context.Players.AddRange(
            new Player { Id = Guid.NewGuid(), Username = "NAPlayer1", Region = "NA" },
            new Player { Id = Guid.NewGuid(), Username = "EUPlayer1", Region = "EU" },
            new Player { Id = Guid.NewGuid(), Username = "NAPlayer2", Region = "NA" }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _playerService.GetAllAsync(1, 20, region: "NA");

        // Assert
        result.Items.Should().HaveCount(2);
        result.Items.Should().OnlyContain(p => p.Region == "NA");
        result.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnSecondPage_Correctly()
    {
        // Arrange
        for (int i = 0; i < 15; i++)
        {
            _context.Players.Add(new Player { Id = Guid.NewGuid(), Username = $"Player{i:D2}" });
        }
        await _context.SaveChangesAsync();

        // Act
        var result = await _playerService.GetAllAsync(page: 2, pageSize: 10);

        // Assert
        result.Items.Should().HaveCount(5);
        result.Page.Should().Be(2);
        result.HasPreviousPage.Should().BeTrue();
        result.HasNextPage.Should().BeFalse();
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnEmpty_WhenNoPlayersInRegion()
    {
        // Arrange
        _context.Players.Add(new Player { Id = Guid.NewGuid(), Username = "NAPlayer", Region = "NA" });
        await _context.SaveChangesAsync();

        // Act
        var result = await _playerService.GetAllAsync(1, 20, region: "SEA");

        // Assert
        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
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
    public async Task UpdateAsync_ShouldReturnNull_WhenNotExists()
    {
        // Arrange
        var updateDto = new UpdatePlayerDto { DisplayName = "Ghost" };

        // Act
        var result = await _playerService.UpdateAsync(Guid.NewGuid(), updateDto);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_ShouldOnlyUpdateProvidedFields()
    {
        // Arrange
        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = "TestUser",
            Email = "original@example.com",
            Bio = "Original bio",
            Region = "NA"
        };
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var updateDto = new UpdatePlayerDto
        {
            DisplayName = "Only This Changes"
            // Email, Bio, Region left null - should not change
        };

        // Act
        var result = await _playerService.UpdateAsync(player.Id, updateDto);

        // Assert
        result!.DisplayName.Should().Be("Only This Changes");
        result.Email.Should().Be("original@example.com");
        result.Bio.Should().Be("Original bio");
        result.Region.Should().Be("NA");
    }

    [Fact]
    public async Task CreateAsync_ShouldMapAllFields_WhenProvided()
    {
        // Arrange
        var createDto = new CreatePlayerDto
        {
            Username = "FullPlayer",
            Email = "full@example.com",
            DisplayName = "Full Player",
            Bio = "My bio",
            Region = "EU",
            AvatarUrl = "https://example.com/avatar.png"
        };

        // Act
        var result = await _playerService.CreateAsync(createDto);

        // Assert
        result.Should().NotBeNull();
        result.Username.Should().Be("FullPlayer");
        result.Email.Should().Be("full@example.com");
        result.DisplayName.Should().Be("Full Player");
        result.Bio.Should().Be("My bio");
        result.Region.Should().Be("EU");
        result.AvatarUrl.Should().Be("https://example.com/avatar.png");
    }

    [Fact]
    public async Task DeleteAsync_ShouldDeletePlayer_Successfully()
    {
        // Arrange
        var player = new Player { Id = Guid.NewGuid(), Username = "PlayerToDelete" };
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        // Act
        var result = await _playerService.DeleteAsync(player.Id);

        // Assert
        result.Should().BeTrue();
        var deletedPlayer = await _context.Players.FindAsync(player.Id);
        deletedPlayer.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ShouldReturnFalse_WhenNotExists()
    {
        // Act
        var result = await _playerService.DeleteAsync(Guid.NewGuid());

        // Assert
        result.Should().BeFalse();
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
