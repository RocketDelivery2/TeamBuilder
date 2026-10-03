using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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

    private async Task<Team> SeedTeamAsync(
        string name = "Test Team",
        Guid? ownerId = null,
        TeamStatus status = TeamStatus.Active,
        int maxMembers = 10)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = name,
            Status = status,
            MaxMembers = maxMembers,
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
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, Guid.NewGuid());
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
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, ownerId);
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
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, Guid.NewGuid());
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
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, ownerId);
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
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, Guid.NewGuid());
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
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, ownerId);
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
        var nonOwnerToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, Guid.NewGuid());
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
        var nonOwnerToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, Guid.NewGuid());
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
        var team = await SeedTeamAsync($"Leave-{Guid.NewGuid():N}", status: TeamStatus.Full, maxMembers: 1);
        var player = await SeedPlayerAsync($"leaver-{Guid.NewGuid():N}");
        await AddMemberAsync(team.Id, player.Id);
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, player.Id);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/teams/{team.Id}/members/{player.Id}/leave");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var membership = await db.TeamMembers.SingleAsync(tm => tm.TeamId == team.Id && tm.PlayerId == player.Id);
        var updatedTeam = await db.Teams.FindAsync(team.Id);
        membership.IsActive.Should().BeFalse();
        updatedTeam!.CurrentMemberCount.Should().Be(0);
        updatedTeam.Status.Should().Be(TeamStatus.Recruiting);
    }

    [Fact]
    public async Task LeaveTeam_ByAnotherAuthenticatedPlayer_Returns403WithoutChangingMembershipOrTeam()
    {
        // Arrange: even the team owner cannot remove another player through the voluntary-leave route.
        var owner = await SeedPlayerAsync($"leaveowner-{Guid.NewGuid():N}");
        var member = await SeedPlayerAsync($"leavemember-{Guid.NewGuid():N}");
        var team = await SeedTeamAsync(
            $"LeaveForbidden-{Guid.NewGuid():N}",
            owner.Id,
            TeamStatus.Full,
            maxMembers: 1);
        await AddMemberAsync(team.Id, member.Id);
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, owner.Id);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/teams/{team.Id}/members/{member.Id}/leave");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var membership = await db.TeamMembers.SingleAsync(tm => tm.TeamId == team.Id && tm.PlayerId == member.Id);
        var unchangedTeam = await db.Teams.FindAsync(team.Id);
        membership.IsActive.Should().BeTrue();
        unchangedTeam!.CurrentMemberCount.Should().Be(1);
        unchangedTeam.Status.Should().Be(TeamStatus.Full);
    }

    [Fact]
    public async Task LeaveTeam_WhenMemberDoesNotExist_Returns404()
    {
        // Arrange
        var team = await SeedTeamAsync($"LeaveNotFound-{Guid.NewGuid():N}");
        var playerId = Guid.NewGuid();
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, playerId);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/teams/{team.Id}/members/{playerId}/leave");
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

    // ── external identity resolution (ExternalIdentity scheme + PlayerIdentity) ──

    private async Task<int> CountTeamsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        return await db.Teams.CountAsync();
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task Create_AsLinkedPlayerWithNonGuidSubject_SetsOwnerIdToInternalPlayerId()
    {
        // Arrange: the external subject is opaque and not a GUID.
        var player = await SeedPlayerAsync($"linkedowner-{Guid.NewGuid():N}");
        var subject = $"auth0|{Guid.NewGuid():N}";
        await LinkedPlayerTokens.LinkAsync(_factory.Services, player.Id, subject, TeamBuilderWebApplicationFactory.TestIssuer);
        var dto = new CreateTeamDto { Name = $"Linked-{Guid.NewGuid():N}", MaxMembers = 5 };
        using var request = Authorized(HttpMethod.Post, "/api/v1/teams", LinkedPlayerTokens.ForSubject(subject), dto);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<TeamDto>();
        created!.OwnerId.Should().Be(player.Id);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var stored = await db.Teams.AsNoTracking().SingleAsync(t => t.Id == created.Id);
        stored.OwnerId.Should().Be(player.Id);
    }

    [Fact]
    public async Task Create_AsUnlinkedIdentity_Returns403AndDoesNotPersistTeam()
    {
        // Arrange
        var before = await CountTeamsAsync();
        var dto = new CreateTeamDto { Name = $"Unlinked-{Guid.NewGuid():N}", MaxMembers = 5 };
        using var request = Authorized(HttpMethod.Post, "/api/v1/teams", LinkedPlayerTokens.ForSubject(LinkedPlayerTokens.NewSubject()), dto);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CountTeamsAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Create_WithGuidSubjectMatchingUnlinkedPlayerId_Returns403()
    {
        // Arrange: a GUID subject equal to an existing Player.Id is not a link. Migrated endpoints
        // never fall back to treating the token's subject as the Player.Id.
        var player = await SeedPlayerAsync($"unlinked-{Guid.NewGuid():N}");
        var before = await CountTeamsAsync();
        var dto = new CreateTeamDto { Name = $"GuidSub-{Guid.NewGuid():N}", MaxMembers = 5 };
        using var request = Authorized(HttpMethod.Post, "/api/v1/teams", TeamBuilderWebApplicationFactory.CreateTestJwt(player.Id.ToString()), dto);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CountTeamsAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Update_AsUnlinkedIdentity_Returns403AndLeavesTeamUnchanged()
    {
        // Arrange
        var owner = await SeedPlayerAsync($"updowner-{Guid.NewGuid():N}");
        var team = await SeedTeamAsync($"UpdUnlinked-{Guid.NewGuid():N}", owner.Id);
        var dto = new UpdateTeamDto { Name = "Unlinked rename" };
        using var request = Authorized(HttpMethod.Put, $"/api/v1/teams/{team.Id}", LinkedPlayerTokens.ForSubject(LinkedPlayerTokens.NewSubject()), dto);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id)).Name.Should().Be(team.Name);
    }

    [Fact]
    public async Task Delete_AsUnlinkedIdentity_Returns403AndKeepsTeam()
    {
        // Arrange
        var owner = await SeedPlayerAsync($"delowner-{Guid.NewGuid():N}");
        var team = await SeedTeamAsync($"DelUnlinked-{Guid.NewGuid():N}", owner.Id);
        using var request = Authorized(HttpMethod.Delete, $"/api/v1/teams/{team.Id}", LinkedPlayerTokens.ForSubject(LinkedPlayerTokens.NewSubject()));

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        (await db.Teams.AnyAsync(t => t.Id == team.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAndDelete_AsOwnerLinkedThroughNonGuidSubject_Succeed()
    {
        // Arrange
        var owner = await SeedPlayerAsync($"nonguidowner-{Guid.NewGuid():N}");
        var subject = $"google-oauth2|{Guid.NewGuid():N}";
        await LinkedPlayerTokens.LinkAsync(_factory.Services, owner.Id, subject, TeamBuilderWebApplicationFactory.TestIssuer);
        var team = await SeedTeamAsync($"NonGuidOwner-{Guid.NewGuid():N}", owner.Id);
        var token = LinkedPlayerTokens.ForSubject(subject);

        // Act
        using var update = Authorized(HttpMethod.Put, $"/api/v1/teams/{team.Id}", token, new UpdateTeamDto { Name = "Owner rename" });
        var updateResponse = await _client.SendAsync(update);
        using var delete = Authorized(HttpMethod.Delete, $"/api/v1/teams/{team.Id}", token);
        var deleteResponse = await _client.SendAsync(delete);

        // Assert
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await updateResponse.Content.ReadFromJsonAsync<TeamDto>())!.Name.Should().Be("Owner rename");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task LeaveTeam_AsUnlinkedIdentity_Returns403AndKeepsMembership()
    {
        // Arrange
        var team = await SeedTeamAsync($"LeaveUnlinked-{Guid.NewGuid():N}");
        var player = await SeedPlayerAsync($"leaveunlinked-{Guid.NewGuid():N}");
        await AddMemberAsync(team.Id, player.Id);
        using var request = Authorized(HttpMethod.Post, $"/api/v1/teams/{team.Id}/members/{player.Id}/leave",
            LinkedPlayerTokens.ForSubject(LinkedPlayerTokens.NewSubject()));

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        (await db.TeamMembers.SingleAsync(tm => tm.TeamId == team.Id && tm.PlayerId == player.Id)).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task LeaveTeam_WithGuidSubjectEqualToRoutePlayerIdButNoLink_Returns403()
    {
        // Arrange: under the legacy context a sub equal to the route playerId was enough to leave.
        var team = await SeedTeamAsync($"LeaveGuidSub-{Guid.NewGuid():N}");
        var player = await SeedPlayerAsync($"leaveguidsub-{Guid.NewGuid():N}");
        await AddMemberAsync(team.Id, player.Id);
        using var request = Authorized(HttpMethod.Post, $"/api/v1/teams/{team.Id}/members/{player.Id}/leave",
            TeamBuilderWebApplicationFactory.CreateTestJwt(player.Id.ToString()));

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
