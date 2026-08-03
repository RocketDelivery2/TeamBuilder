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
/// Verifies that <c>ICurrentUserContext</c> resolves the caller identity correctly.
/// The authenticated JWT <c>sub</c> claim is the only supported identity source.
/// Write endpoints require a JWT Bearer token; anonymous callers receive <c>Guid.Empty</c>.
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

    private static HttpRequestMessage BuildCreateTeamRequest(string? bearerToken = null, Guid? userId = null)
    {
        var dto = new CreateTeamDto { Name = $"Team-{Guid.NewGuid():N}", MaxMembers = 5 };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/teams");
        request.Content = JsonContent.Create(dto);
        if (bearerToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (userId is not null)
            request.Headers.Add("X-User-Id", userId.Value.ToString());
        return request;
    }

    private static HttpRequestMessage BuildCreateJoinRequestRequest(Guid teamId, string? bearerToken = null, Guid? userId = null)
    {
        var dto = new CreateJoinRequestDto { TeamId = teamId };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/joinrequests");
        request.Content = JsonContent.Create(dto);
        if (bearerToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (userId is not null)
            request.Headers.Add("X-User-Id", userId.Value.ToString());
        return request;
    }
    // ── JWT identity resolution ──────────────────────────────────────────────

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

    [Fact]
    public async Task CreateTeam_WithJwtAndXUserIdHeader_PrefersJwtSubClaim()
    {
        // Arrange
        var jwtUserId = Guid.NewGuid();
        var headerUserId = Guid.NewGuid();
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(jwtUserId);
        using var request = BuildCreateTeamRequest(bearerToken: token, userId: headerUserId);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var team = await response.Content.ReadFromJsonAsync<TeamDto>();
        team!.OwnerId.Should().Be(jwtUserId);
    }

    [Fact]
    public async Task CreateTeam_WithJwtMissingSubClaim_IgnoresXUserIdHeader()
    {
        // Arrange
        var headerUserId = Guid.NewGuid();
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(null);
        using var request = BuildCreateTeamRequest(bearerToken: token, userId: headerUserId);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var team = await response.Content.ReadFromJsonAsync<TeamDto>();
        team!.OwnerId.Should().Be(Guid.Empty);
    }

    // ── Missing/invalid claim resolves to Guid.Empty ────────────────────────

    [Fact]
    public async Task CreateTeam_WithJwtMissingSubClaim_ReturnsCreatedWithEmptyOwnerId()
    {
        // A JWT with no sub claim resolves to Guid.Empty as the caller ID.
        // The team is created with OwnerId = Guid.Empty, and ownership checks
        // then treat the caller as a non-owner of any real resource.
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(Guid.Empty);
        using var request = BuildCreateTeamRequest(bearerToken: token);

        var response = await _client.SendAsync(request);

        // Guid.Empty is a valid (if unusual) GUID; the team is created.
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var team = await response.Content.ReadFromJsonAsync<TeamDto>();
        team!.OwnerId.Should().Be(Guid.Empty);
    }
}
