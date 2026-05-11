using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Tests.Integration;

public sealed class PlayersControllerIntegrationTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly TeamBuilderWebApplicationFactory _factory;

    public PlayersControllerIntegrationTests(TeamBuilderWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<Player> SeedPlayerAsync(string username = "testplayer")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = $"{username}@example.com",
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        };

        db.Players.Add(player);
        await db.SaveChangesAsync();
        return player;
    }

    // ── GET /api/v1/players/{id} ─────────────────────────────────────────────

    [Fact]
    public async Task GetById_WhenPlayerExists_Returns200WithPlayerDto()
    {
        // Arrange
        var player = await SeedPlayerAsync($"getbyid-{Guid.NewGuid():N}");

        // Act
        var response = await _client.GetAsync($"/api/v1/players/{player.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<PlayerDto>();
        dto.Should().NotBeNull();
        dto!.Id.Should().Be(player.Id);
        dto.Username.Should().Be(player.Username);
    }

    [Fact]
    public async Task GetById_WhenPlayerDoesNotExist_Returns404()
    {
        // Arrange
        var missingId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync($"/api/v1/players/{missingId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /api/v1/players/username/{username} ──────────────────────────────

    [Fact]
    public async Task GetByUsername_WhenPlayerExists_Returns200WithPlayerDto()
    {
        // Arrange
        var username = $"user-{Guid.NewGuid():N}";
        var player = await SeedPlayerAsync(username);

        // Act
        var response = await _client.GetAsync($"/api/v1/players/username/{username}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<PlayerDto>();
        dto!.Username.Should().Be(username);
    }

    [Fact]
    public async Task GetByUsername_WhenPlayerDoesNotExist_Returns404()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/players/username/no-such-player");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /api/v1/players (paginated) ─────────────────────────────────────

    [Fact]
    public async Task GetAll_Returns200WithPaginatedEnvelope()
    {
        // Arrange — seed one known player so the list is non-empty
        await SeedPlayerAsync($"list-{Guid.NewGuid():N}");

        // Act
        var response = await _client.GetAsync("/api/v1/players?page=1&pageSize=5");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<PaginatedResult<PlayerDto>>();
        result.Should().NotBeNull();
        result!.Items.Should().NotBeNull();
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(5);
    }

    // ── POST /api/v1/players ─────────────────────────────────────────────────

    [Fact]
    public async Task Create_WithValidPayload_Returns201WithCreatedPlayer()
    {
        // Arrange
        var dto = new CreatePlayerDto
        {
            Username = $"new-{Guid.NewGuid():N}",
            Email = "new@example.com"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/players", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<PlayerDto>();
        created!.Username.Should().Be(dto.Username);
        response.Headers.Location.Should().NotBeNull();
    }

    [Fact]
    public async Task Create_WithDuplicateUsername_Returns400()
    {
        // Arrange — seed the player first so the username is taken
        var username = $"dup-{Guid.NewGuid():N}";
        await SeedPlayerAsync(username);

        var dto = new CreatePlayerDto { Username = username };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/players", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── PUT /api/v1/players/{id} ─────────────────────────────────────────────

    [Fact]
    public async Task Update_WhenPlayerExists_Returns200WithUpdatedFields()
    {
        // Arrange
        var player = await SeedPlayerAsync($"upd-{Guid.NewGuid():N}");
        var dto = new UpdatePlayerDto
        {
            DisplayName = "Updated Name",
            Bio = "Updated bio",
            Region = "EU"
        };

        // Act
        var response = await _client.PutAsJsonAsync($"/api/v1/players/{player.Id}", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<PlayerDto>();
        updated!.Id.Should().Be(player.Id);
        updated.DisplayName.Should().Be("Updated Name");
        updated.Bio.Should().Be("Updated bio");
        updated.Region.Should().Be("EU");
    }

    [Fact]
    public async Task Update_WhenPlayerDoesNotExist_Returns404()
    {
        // Arrange
        var dto = new UpdatePlayerDto { DisplayName = "Ghost" };

        // Act
        var response = await _client.PutAsJsonAsync($"/api/v1/players/{Guid.NewGuid()}", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_WithInvalidPayload_Returns400()
    {
        // Arrange — Email violates [EmailAddress]
        var player = await SeedPlayerAsync($"inv-upd-{Guid.NewGuid():N}");
        var dto = new UpdatePlayerDto { Email = "not-an-email" };

        // Act
        var response = await _client.PutAsJsonAsync($"/api/v1/players/{player.Id}", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── DELETE /api/v1/players/{id} ──────────────────────────────────────────

    [Fact]
    public async Task Delete_WhenPlayerExists_Returns204()
    {
        // Arrange
        var player = await SeedPlayerAsync($"del-{Guid.NewGuid():N}");

        // Act
        var response = await _client.DeleteAsync($"/api/v1/players/{player.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_WhenPlayerDoesNotExist_Returns404()
    {
        // Act
        var response = await _client.DeleteAsync($"/api/v1/players/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
