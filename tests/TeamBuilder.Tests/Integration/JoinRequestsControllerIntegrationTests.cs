using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
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

    private async Task<(Team team, Player player)> SeedTeamAndPlayerAsync(
        int maxMembers = 10,
        int currentMemberCount = 0,
        TeamStatus status = TeamStatus.Active,
        Guid? ownerId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = $"Team-{Guid.NewGuid():N}",
            Status = status,
            MaxMembers = maxMembers,
            CurrentMemberCount = currentMemberCount,
            OwnerId = ownerId ?? Guid.NewGuid(),
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

    private async Task<int> GetJoinRequestCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        return await db.JoinRequests.CountAsync();
    }

    private async Task<(JoinRequest joinRequest, Team team, int teamMemberCount)> GetProcessingStateAsync(Guid joinRequestId, Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var joinRequest = await db.JoinRequests.AsNoTracking().SingleAsync(jr => jr.Id == joinRequestId);
        var team = await db.Teams.AsNoTracking().SingleAsync(t => t.Id == teamId);
        var teamMemberCount = await db.TeamMembers.CountAsync(tm => tm.TeamId == teamId);
        return (joinRequest, team, teamMemberCount);
    }

    private async Task<HttpResponseMessage> SendProcessAsync(Guid joinRequestId, RequestStatus status, Guid callerId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/joinrequests/{joinRequestId}/process");
        request.Content = JsonContent.Create(new ProcessJoinRequestDto { Status = status });
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TeamBuilderWebApplicationFactory.CreateTestJwt(callerId));
        return await _client.SendAsync(request);
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
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(player.Id);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/joinrequests");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<JoinRequestDto>();
        created!.TeamId.Should().Be(team.Id);
        created.PlayerId.Should().Be(player.Id);
        created.Status.Should().Be(RequestStatus.Pending);
    }

    [Fact]
    public async Task Create_WithoutJwt_Returns401()
    {
        // Arrange
        var (team, _) = await SeedTeamAndPlayerAsync();
        var dto = new CreateJoinRequestDto { TeamId = team.Id };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/joinrequests", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_WithMissingConfiguredPlayerClaim_Returns401AndDoesNotPersistJoinRequest()
    {
        // Arrange
        var (team, _) = await SeedTeamAndPlayerAsync();
        var before = await GetJoinRequestCountAsync();
        var dto = new CreateJoinRequestDto { TeamId = team.Id };
        var token = TeamBuilderWebApplicationFactory.CreateTestJwtWithPlayerClaim(null, includePlayerClaim: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/joinrequests");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await GetJoinRequestCountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Create_DuplicatePendingRequest_Returns409Conflict()
    {
        // Arrange — seed an already-pending request for the same team/player
        var (team, player) = await SeedTeamAndPlayerAsync();
        await SeedPendingJoinRequestAsync(team.Id, player.Id);

        var dto = new CreateJoinRequestDto { TeamId = team.Id };
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(player.Id);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/joinrequests");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

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
    public async Task Process_ApproveExistingRequestAsTeamOwner_Returns200WithApprovedStatus()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);
        var processDto = new ProcessJoinRequestDto { Status = RequestStatus.Approved };
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(team.OwnerId!.Value);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/joinrequests/{jr.Id}/process");
        request.Content = JsonContent.Create(processDto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JoinRequestDto>();
        result!.Status.Should().Be(RequestStatus.Approved);
    }

    [Fact]
    public async Task Process_RejectExistingRequestAsTeamOwner_Returns200WithRejectedStatus()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);
        var processDto = new ProcessJoinRequestDto { Status = RequestStatus.Rejected };
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(team.OwnerId!.Value);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/joinrequests/{jr.Id}/process");
        request.Content = JsonContent.Create(processDto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JoinRequestDto>();
        result!.Status.Should().Be(RequestStatus.Rejected);
    }

    [Fact]
    public async Task Process_ApproveFullTeam_Returns409Conflict()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync(maxMembers: 1, currentMemberCount: 1, status: TeamStatus.Full);
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);
        var processDto = new ProcessJoinRequestDto { Status = RequestStatus.Approved };
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(team.OwnerId!.Value);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/joinrequests/{jr.Id}/process");
        request.Content = JsonContent.Create(processDto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Process_WhenJoinRequestDoesNotExist_Returns404()
    {
        // Arrange
        var processDto = new ProcessJoinRequestDto { Status = RequestStatus.Approved };
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/joinrequests/{Guid.NewGuid()}/process");
        request.Content = JsonContent.Create(processDto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Process_WithoutJwt_Returns401()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);
        var processDto = new ProcessJoinRequestDto { Status = RequestStatus.Approved };

        // Act
        var response = await _client.PutAsJsonAsync($"/api/v1/joinrequests/{jr.Id}/process", processDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Process_ApproveAsTeamOwner_CreatesTeamMemberAndRecordsOwnerAsProcessor()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);

        // Act
        var response = await SendProcessAsync(jr.Id, RequestStatus.Approved, team.OwnerId!.Value);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (joinRequest, updatedTeam, teamMemberCount) = await GetProcessingStateAsync(jr.Id, team.Id);
        joinRequest.Status.Should().Be(RequestStatus.Approved);
        joinRequest.ProcessedByUserId.Should().Be(team.OwnerId);
        updatedTeam.CurrentMemberCount.Should().Be(1);
        teamMemberCount.Should().Be(1);
    }

    [Theory]
    [InlineData(RequestStatus.Approved)]
    [InlineData(RequestStatus.Rejected)]
    public async Task Process_AsUnrelatedAuthenticatedUser_Returns403AndLeavesStateUnchanged(RequestStatus status)
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);

        // Act
        var response = await SendProcessAsync(jr.Id, status, Guid.NewGuid());

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var (joinRequest, updatedTeam, teamMemberCount) = await GetProcessingStateAsync(jr.Id, team.Id);
        joinRequest.Status.Should().Be(RequestStatus.Pending);
        joinRequest.ProcessedAtUtc.Should().BeNull();
        joinRequest.ProcessedByUserId.Should().BeNull();
        updatedTeam.CurrentMemberCount.Should().Be(0);
        updatedTeam.Status.Should().Be(TeamStatus.Active);
        teamMemberCount.Should().Be(0);
    }

    [Fact]
    public async Task Process_AsApplicantWhoIsNotTeamOwner_Returns403AndDoesNotCreateTeamMember()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);

        // Act
        var response = await SendProcessAsync(jr.Id, RequestStatus.Approved, player.Id);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var (joinRequest, updatedTeam, teamMemberCount) = await GetProcessingStateAsync(jr.Id, team.Id);
        joinRequest.Status.Should().Be(RequestStatus.Pending);
        joinRequest.ProcessedByUserId.Should().BeNull();
        updatedTeam.CurrentMemberCount.Should().Be(0);
        teamMemberCount.Should().Be(0);
    }

    [Fact]
    public async Task Process_AsApplicantWhoIsAlsoTeamOwner_Returns200()
    {
        // Arrange
        var applicantOwnerId = Guid.NewGuid();
        var (team, _) = await SeedTeamAndPlayerAsync(ownerId: applicantOwnerId);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
            db.Players.Add(new Player
            {
                Id = applicantOwnerId,
                Username = $"owner-{Guid.NewGuid():N}",
                CreatedAtUtc = DateTime.UtcNow,
                RowVersion = []
            });
            await db.SaveChangesAsync();
        }
        var jr = await SeedPendingJoinRequestAsync(team.Id, applicantOwnerId);

        // Act
        var response = await SendProcessAsync(jr.Id, RequestStatus.Approved, applicantOwnerId);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (joinRequest, _, teamMemberCount) = await GetProcessingStateAsync(jr.Id, team.Id);
        joinRequest.Status.Should().Be(RequestStatus.Approved);
        teamMemberCount.Should().Be(1);
    }

    [Theory]
    [InlineData(TeamRole.Leader)]
    [InlineData(TeamRole.CoLeader)]
    [InlineData(TeamRole.Officer)]
    [InlineData(TeamRole.Member)]
    [InlineData(TeamRole.Recruit)]
    public async Task Process_AsNonOwnerTeamMemberWithAnyRole_Returns403(TeamRole role)
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync(currentMemberCount: 1);
        var memberId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
            db.Players.Add(new Player
            {
                Id = memberId,
                Username = $"member-{Guid.NewGuid():N}",
                CreatedAtUtc = DateTime.UtcNow,
                RowVersion = []
            });
            db.TeamMembers.Add(new TeamMember
            {
                Id = Guid.NewGuid(),
                TeamId = team.Id,
                PlayerId = memberId,
                Role = role,
                JoinedAtUtc = DateTime.UtcNow,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
                RowVersion = []
            });
            await db.SaveChangesAsync();
        }
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);

        // Act
        var response = await SendProcessAsync(jr.Id, RequestStatus.Approved, memberId);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var (joinRequest, updatedTeam, teamMemberCount) = await GetProcessingStateAsync(jr.Id, team.Id);
        joinRequest.Status.Should().Be(RequestStatus.Pending);
        updatedTeam.CurrentMemberCount.Should().Be(1);
        teamMemberCount.Should().Be(1);
    }

    [Fact]
    public async Task Process_AsNonOwnerOnAlreadyProcessedRequest_Returns403()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);
        (await SendProcessAsync(jr.Id, RequestStatus.Rejected, team.OwnerId!.Value)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Act
        var response = await SendProcessAsync(jr.Id, RequestStatus.Approved, Guid.NewGuid());

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Process_AsTeamOwnerOnAlreadyProcessedRequest_Returns409Conflict()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);
        (await SendProcessAsync(jr.Id, RequestStatus.Rejected, team.OwnerId!.Value)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Act
        var response = await SendProcessAsync(jr.Id, RequestStatus.Approved, team.OwnerId!.Value);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Process_AsNonOwnerOnFullTeam_Returns403()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync(maxMembers: 1, currentMemberCount: 1, status: TeamStatus.Full);
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);

        // Act
        var response = await SendProcessAsync(jr.Id, RequestStatus.Approved, Guid.NewGuid());

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
