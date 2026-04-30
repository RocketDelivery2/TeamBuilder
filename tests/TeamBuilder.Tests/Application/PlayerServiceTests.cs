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

    public void Dispose()
    {
        _context.Dispose();
    }
}
