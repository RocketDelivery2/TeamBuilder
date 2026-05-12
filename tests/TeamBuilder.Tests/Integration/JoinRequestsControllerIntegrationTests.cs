using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Tests.Integration;

public sealed class JoinRequestsControllerIntegrationTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly TeamBuilderWebApplicationFactory _factory;

    public JoinRequestsControllerIntegrationTests(TeamBuilderWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<(Team team, Player player)> SeedTeamAndPlayerAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = $"Team-{Guid.NewGuid():N}",
            Status = TeamStatus.Active,
            MaxMembers = 10,
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        };

        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = $"player-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        };

        db.Teams.Add(team);
        db.Players.Add(player);
        await db.SaveChangesAsync();
        return (team, player);
    }

    private async Task<JoinRequest> SeedPendingJoinRequestAsync(Guid teamId, Guid playerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

        var jr = new JoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            PlayerId = playerId,
            Status = RequestStatus.Pending,
            RequestedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        };

        db.JoinRequests.Add(jr);
        await db.SaveChangesAsync();
        return jr;
    }

    // ── GET /api/v1/joinrequests/{id} ────────────────────────────────────────

    [Fact]
    public async Task GetById_WhenJoinRequestExists_Returns200()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);

        // Act
        var response = await _client.GetAsync($"/api/v1/joinrequests/{jr.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<JoinRequestDto>();
        dto!.Id.Should().Be(jr.Id);
        dto.Status.Should().Be(RequestStatus.Pending);
    }

    [Fact]
    public async Task GetById_WhenJoinRequestDoesNotExist_Returns404()
    {
        // Act
        var response = await _client.GetAsync($"/api/v1/joinrequests/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /api/v1/joinrequests ────────────────────────────────────────────

    [Fact]
    public async Task Create_WithValidPayload_Returns201()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var dto = new CreateJoinRequestDto { TeamId = team.Id, Message = "Please let me join!" };

        // Act — X-User-Id header acts as the authenticated player identity
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/joinrequests");
        request.Content = JsonContent.Create(dto);
        request.Headers.Add("X-User-Id", player.Id.ToString());
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<JoinRequestDto>();
        created!.TeamId.Should().Be(team.Id);
        created.PlayerId.Should().Be(player.Id);
        created.Status.Should().Be(RequestStatus.Pending);
    }

    [Fact]
    public async Task Create_DuplicatePendingRequest_Returns409Conflict()
    {
        // Arrange — seed an already-pending request for the same team/player
        var (team, player) = await SeedTeamAndPlayerAsync();
        await SeedPendingJoinRequestAsync(team.Id, player.Id);

        var dto = new CreateJoinRequestDto { TeamId = team.Id };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/joinrequests");
        request.Content = JsonContent.Create(dto);
        request.Headers.Add("X-User-Id", player.Id.ToString());

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status409Conflict);
    }

    // ── PUT /api/v1/joinrequests/{id}/process ────────────────────────────────

    [Fact]
    public async Task Process_ApproveExistingRequest_Returns200WithApprovedStatus()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);
        var processDto = new ProcessJoinRequestDto { Status = RequestStatus.Approved };

        // Act
        var response = await _client.PutAsJsonAsync($"/api/v1/joinrequests/{jr.Id}/process", processDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JoinRequestDto>();
        result!.Status.Should().Be(RequestStatus.Approved);
    }

    [Fact]
    public async Task Process_RejectExistingRequest_Returns200WithRejectedStatus()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);
        var processDto = new ProcessJoinRequestDto { Status = RequestStatus.Rejected };

        // Act
        var response = await _client.PutAsJsonAsync($"/api/v1/joinrequests/{jr.Id}/process", processDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JoinRequestDto>();
        result!.Status.Should().Be(RequestStatus.Rejected);
    }

    [Fact]
    public async Task Process_WhenJoinRequestDoesNotExist_Returns404()
    {
        // Arrange
        var processDto = new ProcessJoinRequestDto { Status = RequestStatus.Approved };

        // Act
        var response = await _client.PutAsJsonAsync($"/api/v1/joinrequests/{Guid.NewGuid()}/process", processDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
