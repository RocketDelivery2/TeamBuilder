using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// Verifies that the <c>ICurrentUserContext</c> / <c>HeaderCurrentUserContext</c>
/// abstraction behaves correctly for all three X-User-Id header states:
/// valid GUID, missing header, and unparseable value.
/// </summary>
public sealed class CurrentUserContextIntegrationTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly TeamBuilderWebApplicationFactory _factory;

    public CurrentUserContextIntegrationTests(TeamBuilderWebApplicationFactory factory)
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

    private static HttpRequestMessage BuildCreateTeamRequest(string? xUserId)
    {
        var dto = new CreateTeamDto { Name = $"Team-{Guid.NewGuid():N}", MaxMembers = 5 };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/teams");
        request.Content = JsonContent.Create(dto);
        if (xUserId is not null)
            request.Headers.Add("X-User-Id", xUserId);
        return request;
    }

    private static HttpRequestMessage BuildCreateJoinRequestRequest(Guid teamId, string? xUserId)
    {
        var dto = new CreateJoinRequestDto { TeamId = teamId };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/joinrequests");
        request.Content = JsonContent.Create(dto);
        if (xUserId is not null)
            request.Headers.Add("X-User-Id", xUserId);
        return request;
    }

    // ── Valid X-User-Id header ────────────────────────────────────────────────

    [Fact]
    public async Task CreateTeam_WithValidXUserId_SetsOwnerIdOnCreatedTeam()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        using var request = BuildCreateTeamRequest(ownerId.ToString());

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var team = await response.Content.ReadFromJsonAsync<TeamDto>();
        team!.OwnerId.Should().Be(ownerId);
    }

    [Fact]
    public async Task CreateJoinRequest_WithValidXUserId_SetsPlayerIdOnJoinRequest()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        using var request = BuildCreateJoinRequestRequest(team.Id, player.Id.ToString());

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var jr = await response.Content.ReadFromJsonAsync<JoinRequestDto>();
        jr!.PlayerId.Should().Be(player.Id);
    }

    // ── Missing X-User-Id header ──────────────────────────────────────────────

    [Fact]
    public async Task CreateTeam_WithoutXUserId_SetsOwnerIdToGuidEmpty()
    {
        // Arrange — no X-User-Id header
        using var request = BuildCreateTeamRequest(xUserId: null);

        // Act
        var response = await _client.SendAsync(request);

        // Assert — request succeeds, owner defaults to Guid.Empty
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var team = await response.Content.ReadFromJsonAsync<TeamDto>();
        team!.OwnerId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task CreateJoinRequest_WithoutXUserId_SetsPlayerIdToGuidEmpty()
    {
        // Arrange
        var (team, _) = await SeedTeamAndPlayerAsync();
        using var request = BuildCreateJoinRequestRequest(team.Id, xUserId: null);

        // Act
        var response = await _client.SendAsync(request);

        // Assert — request succeeds, playerId defaults to Guid.Empty
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var jr = await response.Content.ReadFromJsonAsync<JoinRequestDto>();
        jr!.PlayerId.Should().Be(Guid.Empty);
    }

    // ── Invalid (unparseable) X-User-Id header ────────────────────────────────

    [Fact]
    public async Task CreateTeam_WithInvalidXUserId_SetsOwnerIdToGuidEmpty()
    {
        // Arrange — header value is not a valid GUID
        using var request = BuildCreateTeamRequest(xUserId: "not-a-guid");

        // Act
        var response = await _client.SendAsync(request);

        // Assert — request still succeeds; invalid header falls back to Guid.Empty
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var team = await response.Content.ReadFromJsonAsync<TeamDto>();
        team!.OwnerId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task CreateJoinRequest_WithInvalidXUserId_SetsPlayerIdToGuidEmpty()
    {
        // Arrange
        var (team, _) = await SeedTeamAndPlayerAsync();
        using var request = BuildCreateJoinRequestRequest(team.Id, xUserId: "not-a-guid");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var jr = await response.Content.ReadFromJsonAsync<JoinRequestDto>();
        jr!.PlayerId.Should().Be(Guid.Empty);
    }

    // ── JWT Bearer claims path ────────────────────────────────────────────────

    [Fact]
    public async Task CreateTeam_WithValidJwt_SetsOwnerIdFromSubClaim()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(ownerId);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/teams");
        request.Content = JsonContent.Create(new CreateTeamDto { Name = $"Team-{Guid.NewGuid():N}", MaxMembers = 5 });
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var team = await response.Content.ReadFromJsonAsync<TeamDto>();
        team!.OwnerId.Should().Be(ownerId);
    }

    [Fact]
    public async Task CreateJoinRequest_WithValidJwt_SetsPlayerIdFromSubClaim()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(player.Id);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/joinrequests");
        request.Content = JsonContent.Create(new CreateJoinRequestDto { TeamId = team.Id });
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var jr = await response.Content.ReadFromJsonAsync<JoinRequestDto>();
        jr!.PlayerId.Should().Be(player.Id);
    }

    [Fact]
    public async Task CreateTeam_WithJwtAndXUserId_UsesJwtSubClaimNotHeader()
    {
        // Arrange — both JWT and X-User-Id present; JWT claims take priority
        var jwtUserId = Guid.NewGuid();
        var headerId = Guid.NewGuid();
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(jwtUserId);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/teams");
        request.Content = JsonContent.Create(new CreateTeamDto { Name = $"Team-{Guid.NewGuid():N}", MaxMembers = 5 });
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-User-Id", headerId.ToString());

        // Act
        var response = await _client.SendAsync(request);

        // Assert — JWT sub claim wins
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var team = await response.Content.ReadFromJsonAsync<TeamDto>();
        team!.OwnerId.Should().Be(jwtUserId);
    }
}

