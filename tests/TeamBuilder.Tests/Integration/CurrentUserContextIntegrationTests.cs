using System.Net;
using System.Net.Http.Headers;
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
/// Verifies that <c>ICurrentUserContext</c> resolves the caller identity correctly:
/// JWT Bearer <c>sub</c> claim takes priority; <c>X-User-Id</c> header is the fallback
/// for unauthenticated endpoints. Write endpoints now require a JWT Bearer token.
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

    private static HttpRequestMessage BuildCreateTeamRequest(string? bearerToken, string? xUserId = null)
    {
        var dto = new CreateTeamDto { Name = $"Team-{Guid.NewGuid():N}", MaxMembers = 5 };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/teams");
        request.Content = JsonContent.Create(dto);
        if (bearerToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (xUserId is not null)
            request.Headers.Add("X-User-Id", xUserId);
        return request;
    }

    private static HttpRequestMessage BuildCreateJoinRequestRequest(Guid teamId, string? bearerToken, string? xUserId = null)
    {
        var dto = new CreateJoinRequestDto { TeamId = teamId };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/joinrequests");
        request.Content = JsonContent.Create(dto);
        if (bearerToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (xUserId is not null)
            request.Headers.Add("X-User-Id", xUserId);
        return request;
    }

    // ── JWT Bearer claims path (write endpoints require JWT) ─────────────────

    [Fact]
    public async Task CreateTeam_WithValidJwtSubClaim_SetsOwnerIdFromToken()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(ownerId);
        using var request = BuildCreateTeamRequest(bearerToken: token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var team = await response.Content.ReadFromJsonAsync<TeamDto>();
        team!.OwnerId.Should().Be(ownerId);
    }

    [Fact]
    public async Task CreateJoinRequest_WithValidJwtSubClaim_SetsPlayerIdFromToken()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(player.Id);
        using var request = BuildCreateJoinRequestRequest(team.Id, bearerToken: token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var jr = await response.Content.ReadFromJsonAsync<JoinRequestDto>();
        jr!.PlayerId.Should().Be(player.Id);
    }

    // ── Protected write endpoint — missing JWT returns 401 ───────────────────

    [Fact]
    public async Task CreateTeam_WithoutJwt_Returns401()
    {
        // Arrange — no Authorization header; write endpoint now requires JWT
        using var request = BuildCreateTeamRequest(bearerToken: null);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateJoinRequest_WithoutJwt_Returns401()
    {
        // Arrange
        var (team, _) = await SeedTeamAndPlayerAsync();
        using var request = BuildCreateJoinRequestRequest(team.Id, bearerToken: null);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── X-User-Id ignored when JWT is absent on protected endpoint ────────────

    [Fact]
    public async Task CreateTeam_WithXUserIdButNoJwt_Returns401()
    {
        // X-User-Id alone is not sufficient to access protected write endpoints.
        // A JWT Bearer token is required; the header-only path now returns 401.
        var ownerId = Guid.NewGuid();
        using var request = BuildCreateTeamRequest(bearerToken: null, xUserId: ownerId.ToString());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateJoinRequest_WithXUserIdButNoJwt_Returns401()
    {
        // X-User-Id alone is not sufficient to access protected write endpoints.
        var (team, player) = await SeedTeamAndPlayerAsync();
        using var request = BuildCreateJoinRequestRequest(team.Id, bearerToken: null, xUserId: player.Id.ToString());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── JWT takes priority over X-User-Id header ────────────────────────────

    [Fact]
    public async Task CreateTeam_WithJwtAndXUserId_UsesJwtSubClaimNotHeader()
    {
        // Arrange — both JWT and X-User-Id present; JWT sub claim takes priority
        var jwtUserId = Guid.NewGuid();
        var headerId = Guid.NewGuid();
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(jwtUserId);
        using var request = BuildCreateTeamRequest(bearerToken: token, xUserId: headerId.ToString());

        // Act
        var response = await _client.SendAsync(request);

        // Assert — JWT sub claim wins
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var team = await response.Content.ReadFromJsonAsync<TeamDto>();
        team!.OwnerId.Should().Be(jwtUserId);
    }
}

