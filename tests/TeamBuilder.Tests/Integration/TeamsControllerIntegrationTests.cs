using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Tests.Integration;

public sealed class TeamsControllerIntegrationTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly TeamBuilderWebApplicationFactory _factory;

    public TeamsControllerIntegrationTests(TeamBuilderWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<Team> SeedTeamAsync(string name = "Test Team", Guid? ownerId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = name,
            Status = TeamStatus.Active,
            MaxMembers = 10,
            CurrentMemberCount = 0,
            OwnerId = ownerId,
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        };

        db.Teams.Add(team);
        await db.SaveChangesAsync();
        return team;
    }

    private async Task<Player> SeedPlayerAsync(string username)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = username,
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        };

        db.Players.Add(player);
        await db.SaveChangesAsync();
        return player;
    }

    private async Task<TeamMember> AddMemberAsync(Guid teamId, Guid playerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

        var membership = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            PlayerId = playerId,
            Role = TeamRole.Member,
            IsActive = true,
            JoinedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        };

        var team = await db.Teams.FindAsync(teamId);
        if (team is not null)
            team.CurrentMemberCount++;

        db.TeamMembers.Add(membership);
        await db.SaveChangesAsync();
        return membership;
    }

    // ── GET /api/v1/teams/{id} ───────────────────────────────────────────────

    [Fact]
    public async Task GetById_WhenTeamExists_Returns200WithTeamDto()
    {
        // Arrange
        var team = await SeedTeamAsync($"GetById-{Guid.NewGuid():N}");

        // Act
        var response = await _client.GetAsync($"/api/v1/teams/{team.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<TeamDto>();
        dto!.Id.Should().Be(team.Id);
        dto.Name.Should().Be(team.Name);
    }

    [Fact]
    public async Task GetById_WhenTeamDoesNotExist_Returns404()
    {
        // Act
        var response = await _client.GetAsync($"/api/v1/teams/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /api/v1/teams (paginated) ────────────────────────────────────────

    [Fact]
    public async Task GetAll_Returns200WithPaginatedEnvelope()
    {
        // Arrange
        await SeedTeamAsync($"List-{Guid.NewGuid():N}");

        // Act
        var response = await _client.GetAsync("/api/v1/teams?page=1&pageSize=5");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<PaginatedResult<TeamDto>>();
        result.Should().NotBeNull();
        result!.Page.Should().Be(1);
        result.PageSize.Should().Be(5);
        result.Items.Should().NotBeNull();
    }

    [Fact]
    public async Task GetAll_PageSizeOutOfRange_ClampsTo20()
    {
        // Act — pageSize=0 should be clamped to 20 by the controller
        var response = await _client.GetAsync("/api/v1/teams?page=1&pageSize=0");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<PaginatedResult<TeamDto>>();
        result!.PageSize.Should().Be(20);
    }

    // ── POST /api/v1/teams ───────────────────────────────────────────────────

    [Fact]
    public async Task Create_WithValidPayload_Returns201WithCreatedTeam()
    {
        // Arrange
        var dto = new CreateTeamDto
        {
            Name = $"NewTeam-{Guid.NewGuid():N}",
            MaxMembers = 5
        };
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/teams");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<TeamDto>();
        created!.Name.Should().Be(dto.Name);
        created.MaxMembers.Should().Be(5);
        response.Headers.Location.Should().NotBeNull();
    }

    [Fact]
    public async Task Create_WithoutJwt_Returns401()
    {
        // Arrange
        var dto = new CreateTeamDto { Name = $"Unauth-{Guid.NewGuid():N}", MaxMembers = 5 };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/teams", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── DELETE /api/v1/teams/{id} ────────────────────────────────────────────

    [Fact]
    public async Task Delete_WhenTeamExists_Returns204()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var team = await SeedTeamAsync($"Del-{Guid.NewGuid():N}", ownerId);
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(ownerId);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/teams/{team.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_WhenTeamDoesNotExist_Returns404()
    {
        // Arrange
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/teams/{Guid.NewGuid()}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_WithoutJwt_Returns401()
    {
        // Arrange
        var team = await SeedTeamAsync($"DelUnauth-{Guid.NewGuid():N}");

        // Act
        var response = await _client.DeleteAsync($"/api/v1/teams/{team.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── PUT /api/v1/teams/{id} ───────────────────────────────────────────────

    [Fact]
    public async Task Update_WhenTeamExists_Returns200WithUpdatedFields()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var team = await SeedTeamAsync($"Upd-{Guid.NewGuid():N}", ownerId);
        var dto = new UpdateTeamDto
        {
            Name = "Renamed Team",
            Description = "New description",
            Region = "EU"
        };
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(ownerId);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/teams/{team.Id}");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<TeamDto>();
        updated!.Id.Should().Be(team.Id);
        updated.Name.Should().Be("Renamed Team");
        updated.Description.Should().Be("New description");
        updated.Region.Should().Be("EU");
    }

    [Fact]
    public async Task Update_WhenTeamDoesNotExist_Returns404()
    {
        // Arrange
        var dto = new UpdateTeamDto { Name = "Ghost Team" };
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/teams/{Guid.NewGuid()}");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_WithInvalidPayload_Returns400()
    {
        // Arrange — MaxMembers = 0 violates [Range(1, 1000)]
        var ownerId = Guid.NewGuid();
        var team = await SeedTeamAsync($"InvUpd-{Guid.NewGuid():N}", ownerId);
        var dto = new UpdateTeamDto { MaxMembers = 0 };
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(ownerId);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/teams/{team.Id}");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_WithoutJwt_Returns401()
    {
        // Arrange
        var team = await SeedTeamAsync($"UpdUnauth-{Guid.NewGuid():N}");
        var dto = new UpdateTeamDto { Name = "Should fail" };

        // Act
        var response = await _client.PutAsJsonAsync($"/api/v1/teams/{team.Id}", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Update_ByNonOwner_Returns403()
    {
        // Arrange — team has a different owner
        var ownerId = Guid.NewGuid();
        var team = await SeedTeamAsync($"UpdNonOwner-{Guid.NewGuid():N}", ownerId);
        var dto = new UpdateTeamDto { Name = "Non-owner rename" };
        var nonOwnerToken = TeamBuilderWebApplicationFactory.CreateTestJwt(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/teams/{team.Id}");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", nonOwnerToken);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Delete_ByNonOwner_Returns403()
    {
        // Arrange — team has a different owner
        var ownerId = Guid.NewGuid();
        var team = await SeedTeamAsync($"DelNonOwner-{Guid.NewGuid():N}", ownerId);
        var nonOwnerToken = TeamBuilderWebApplicationFactory.CreateTestJwt(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/teams/{team.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", nonOwnerToken);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── POST /api/v1/teams/{teamId}/members/{playerId}/leave ─────────────────

    [Fact]
    public async Task LeaveTeam_WhenMemberExists_Returns204()
    {
        // Arrange
        var team = await SeedTeamAsync($"Leave-{Guid.NewGuid():N}");
        var player = await SeedPlayerAsync($"leaver-{Guid.NewGuid():N}");
        await AddMemberAsync(team.Id, player.Id);
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(player.Id);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/teams/{team.Id}/members/{player.Id}/leave");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task LeaveTeam_WhenMemberDoesNotExist_Returns404()
    {
        // Arrange
        var team = await SeedTeamAsync($"LeaveNotFound-{Guid.NewGuid():N}");
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/teams/{team.Id}/members/{Guid.NewGuid()}/leave");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task LeaveTeam_WithoutJwt_Returns401()
    {
        // Arrange
        var team = await SeedTeamAsync($"LeaveUnauth-{Guid.NewGuid():N}");
        var player = await SeedPlayerAsync($"leaverunauthplayer-{Guid.NewGuid():N}");
        await AddMemberAsync(team.Id, player.Id);

        // Act
        var response = await _client.PostAsync(
            $"/api/v1/teams/{team.Id}/members/{player.Id}/leave", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
