using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// Verifies caller identity resolution on the team and join-request write endpoints. They use the
/// ExternalIdentity scheme: the token's Issuer + Subject resolve to an internal player through
/// <c>PlayerIdentity</c>. A missing or empty subject fails authentication (401); an authenticated
/// subject with no linked player is forbidden (403), whatever the subject looks like.
/// </summary>
public sealed class ExternalIdentityAuthenticationIntegrationTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly TeamBuilderWebApplicationFactory _factory;

    public ExternalIdentityAuthenticationIntegrationTests(TeamBuilderWebApplicationFactory factory)
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
            LifecycleStatus = TeamLifecycleStatus.Active,
            IsAcceptingMembers = true,
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

    private static string CreateEmptySubjectJwt()
        => TeamBuilderWebApplicationFactory.CreateTestJwtWithSubject(string.Empty);

    private static string CreateNonGuidSubjectJwt()
        => TeamBuilderWebApplicationFactory.CreateTestJwtWithSubject("not-a-guid");

    private static string CreateMissingSubjectJwt()
        => TeamBuilderWebApplicationFactory.CreateTestJwtWithSubject(null, includeSubject: false);

    [Fact]
    public void ExternalIdentity_IsDefaultAuthenticateAndChallengeScheme()
    {
        var options = _factory.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;

        options.DefaultAuthenticateScheme.Should().Be(TeamBuilder.Api.Auth.ExternalIdentityAuthentication.SchemeName);
        options.DefaultChallengeScheme.Should().Be(TeamBuilder.Api.Auth.ExternalIdentityAuthentication.SchemeName);
    }

    [Fact]
    public async Task PlainAuthorize_AcceptsOpaqueNonGuidSubject()
    {
        _factory.Services.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items.Select(action => action.DisplayName)
            .Should().Contain(name => name!.Contains(nameof(PlainAuthorizeTestController)));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/__test/plain-authorize");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateNonGuidSubjectJwt());

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("not-a-guid");
    }

    [Fact]
    public async Task PlainAuthorize_AcceptsGuidShapedSubjectAsOpaque()
    {
        var subject = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__test/plain-authorize");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TeamBuilderWebApplicationFactory.CreateTestJwt(subject));

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain(subject);
    }

    [Fact]
    public async Task PlainAuthorize_WithoutConfiguredSubject_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__test/plain-authorize");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateMissingSubjectJwt());

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PlainAuthorize_WithInvalidJwt_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__test/plain-authorize");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.valid.jwt");

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void LegacyCurrentUserContextTypeAndRegistration_AreAbsent()
    {
        var interfaceTypeName = string.Concat("ICurrent", "UserContext");
        var implementationTypeName = "TeamBuilder.Api.Auth." + string.Concat("ClaimsCurrent", "UserContext");
        var legacyInterface = typeof(ICurrentPlayerResolver).Assembly.GetType(interfaceTypeName);
        var legacyImplementation = typeof(Program).Assembly.GetType(implementationTypeName);

        legacyInterface.Should().BeNull();
        legacyImplementation.Should().BeNull();

        _factory.Services.GetRequiredService<IConfiguration>()
            .AsEnumerable()
            .Should()
            .NotContain(setting => setting.Key == string.Concat("Jwt:Player", "IdClaim"));
    }

    // ── JWT identity resolution ──────────────────────────────────────────────

    [Fact]
    public async Task CreateTeam_AsLinkedPlayer_SetsOwnerIdToResolvedPlayerId()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, ownerId);
        using var request = BuildCreateTeamRequest(bearerToken: token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var team = await response.Content.ReadFromJsonAsync<TeamDto>();
        team!.OwnerId.Should().Be(ownerId);
    }

    [Fact]
    public async Task CreateJoinRequest_AsLinkedPlayer_SetsPlayerIdToResolvedPlayerId()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, player.Id);
        using var request = BuildCreateJoinRequestRequest(team.Id, bearerToken: token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var jr = await response.Content.ReadFromJsonAsync<JoinRequestDto>();
        jr!.PlayerId.Should().Be(player.Id);
    }

    [Fact]
    public async Task CreateTeam_AsLinkedPlayer_IgnoresXUserIdHeader()
    {
        // Arrange
        var jwtUserId = Guid.NewGuid();
        var headerUserId = Guid.NewGuid();
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, jwtUserId);
        using var request = BuildCreateTeamRequest(bearerToken: token, userId: headerUserId);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var team = await response.Content.ReadFromJsonAsync<TeamDto>();
        team!.OwnerId.Should().Be(jwtUserId);
    }

    [Fact]
    public async Task CreateTeam_WithMissingSubjectClaimAndXUserIdHeader_ReturnsUnauthorizedAndDoesNotPersistTeam()
    {
        // Arrange
        var before = await GetCountsAsync();
        var headerUserId = Guid.NewGuid();
        var token = CreateMissingSubjectJwt();
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
    public async Task CreateTeam_WithEmptySubjectClaim_ReturnsUnauthorizedAndDoesNotPersistTeam()
    {
        // Arrange
        var before = await GetCountsAsync();
        var token = CreateEmptySubjectJwt();
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
    public async Task CreateTeam_WithUnlinkedNonGuidSubject_ReturnsForbiddenAndDoesNotPersistTeam()
    {
        // A non-GUID subject is a valid external identity, so authentication succeeds; the caller
        // is forbidden only because no player is linked to it.
        // Arrange
        var before = await GetCountsAsync();
        var token = CreateNonGuidSubjectJwt();
        using var request = BuildCreateTeamRequest(bearerToken: token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var after = await GetCountsAsync();
        after.Teams.Should().Be(before.Teams);
        after.JoinRequests.Should().Be(before.JoinRequests);
    }

    [Fact]
    public async Task CreateJoinRequest_WithMissingSubjectClaim_ReturnsUnauthorizedAndDoesNotPersistJoinRequest()
    {
        // Arrange
        var (team, _) = await SeedTeamAndPlayerAsync();
        var before = await GetCountsAsync();
        var token = CreateMissingSubjectJwt();
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
