using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// Verifies that <c>ICurrentUserContext</c> resolves the caller identity correctly.
/// The authenticated JWT uses the configured player claim (default <c>sub</c>).
/// Write endpoints fail closed when that claim is missing, empty, or invalid.
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

    private async Task<(int Teams, int JoinRequests)> GetCountsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        return (
            await db.Teams.CountAsync(),
            await db.JoinRequests.CountAsync());
    }

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

    private static string CreateEmptyPlayerClaimJwt()
        => TeamBuilderWebApplicationFactory.CreateTestJwtWithPlayerClaim(string.Empty);

    private static string CreateMalformedPlayerClaimJwt()
        => TeamBuilderWebApplicationFactory.CreateTestJwtWithPlayerClaim("not-a-guid");

    private static string CreateMissingPlayerClaimJwt()
        => TeamBuilderWebApplicationFactory.CreateTestJwtWithPlayerClaim(null, includePlayerClaim: false);

    // ── JWT identity resolution ──────────────────────────────────────────────

    [Fact]
    public async Task CreateTeam_WithValidConfiguredPlayerClaim_SetsOwnerIdFromToken()
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
    public async Task CreateJoinRequest_WithValidConfiguredPlayerClaim_SetsPlayerIdFromToken()
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
    public async Task CreateTeam_WithValidConfiguredPlayerClaim_IgnoresXUserIdHeader()
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
    public async Task CreateTeam_WithMissingConfiguredPlayerClaimAndXUserIdHeader_ReturnsUnauthorizedAndDoesNotPersistTeam()
    {
        // Arrange
        var before = await GetCountsAsync();
        var headerUserId = Guid.NewGuid();
        var token = CreateMissingPlayerClaimJwt();
        using var request = BuildCreateTeamRequest(bearerToken: token, userId: headerUserId);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var after = await GetCountsAsync();
        after.Teams.Should().Be(before.Teams);
        after.JoinRequests.Should().Be(before.JoinRequests);
    }

    [Fact]
    public async Task CreateTeam_WithEmptyConfiguredPlayerClaim_ReturnsUnauthorizedAndDoesNotPersistTeam()
    {
        // Arrange
        var before = await GetCountsAsync();
        var token = CreateEmptyPlayerClaimJwt();
        using var request = BuildCreateTeamRequest(bearerToken: token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var after = await GetCountsAsync();
        after.Teams.Should().Be(before.Teams);
        after.JoinRequests.Should().Be(before.JoinRequests);
    }

    [Fact]
    public async Task CreateTeam_WithMalformedConfiguredPlayerClaim_ReturnsUnauthorizedAndDoesNotPersistTeam()
    {
        // Arrange
        var before = await GetCountsAsync();
        var token = CreateMalformedPlayerClaimJwt();
        using var request = BuildCreateTeamRequest(bearerToken: token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var after = await GetCountsAsync();
        after.Teams.Should().Be(before.Teams);
        after.JoinRequests.Should().Be(before.JoinRequests);
    }

    [Fact]
    public async Task CreateJoinRequest_WithMissingConfiguredPlayerClaim_ReturnsUnauthorizedAndDoesNotPersistJoinRequest()
    {
        // Arrange
        var (team, _) = await SeedTeamAndPlayerAsync();
        var before = await GetCountsAsync();
        var token = CreateMissingPlayerClaimJwt();
        using var request = BuildCreateJoinRequestRequest(team.Id, bearerToken: token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var after = await GetCountsAsync();
        after.Teams.Should().Be(before.Teams);
        after.JoinRequests.Should().Be(before.JoinRequests);
    }
}
