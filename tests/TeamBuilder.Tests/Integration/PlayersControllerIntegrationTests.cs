using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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

    private static HttpRequestMessage UpdateRequest(Guid id, UpdatePlayerDto dto, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/players/{id}")
        {
            Content = JsonContent.Create(dto)
        };
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage DeleteRequest(Guid id, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/players/{id}");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<Player?> FindPlayerAsync(Guid id, IServiceProvider? services = null)
    {
        using var scope = (services ?? _factory.Services).CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        return await db.Players.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id);
    }

    /// <summary>
    /// Links an identity to a player id that has no Player row, as when the linked player is
    /// deleted between identity resolution and the operation.
    /// </summary>
    private async Task<string> OrphanedIdentityTokenAsync(Guid playerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var subject = LinkedPlayerTokens.NewSubject();
        db.PlayerIdentities.Add(new PlayerIdentity
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            Issuer = TeamBuilderWebApplicationFactory.TestIssuer,
            Subject = subject,
            Provider = "oidc",
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        });
        await db.SaveChangesAsync();
        return LinkedPlayerTokens.ForSubject(subject);
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
    public async Task Create_WithDuplicateUsername_Returns409Conflict()
    {
        // Arrange — seed the player first so the username is taken
        var username = $"dup-{Guid.NewGuid():N}";
        await SeedPlayerAsync(username);

        var dto = new CreatePlayerDto { Username = username };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/players", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status409Conflict);
    }

    // ── PUT /api/v1/players/{id} ─────────────────────────────────────────────

    [Fact]
    public async Task Update_AsLinkedSelf_Returns200WithUpdatedFields()
    {
        // Arrange
        var player = await SeedPlayerAsync($"upd-{Guid.NewGuid():N}");
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, player.Id);
        var dto = new UpdatePlayerDto
        {
            DisplayName = "Updated Name",
            Bio = "Updated bio",
            Region = "EU"
        };

        // Act
        using var request = UpdateRequest(player.Id, dto, token);
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<PlayerDto>();
        updated!.Id.Should().Be(player.Id);
        updated.DisplayName.Should().Be("Updated Name");
        updated.Bio.Should().Be("Updated bio");
        updated.Region.Should().Be("EU");
        (await FindPlayerAsync(player.Id))!.DisplayName.Should().Be("Updated Name");
    }

    [Fact]
    public async Task Update_AsLinkedSelf_WithInvalidPayload_Returns400WithProblemDetails()
    {
        // Arrange — DisplayName exceeds MaxLength
        var player = await SeedPlayerAsync($"inv-{Guid.NewGuid():N}");
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, player.Id);
        var dto = new UpdatePlayerDto { DisplayName = new string('x', 300) };

        // Act
        using var request = UpdateRequest(player.Id, dto, token);
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status400BadRequest);
        problem.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Update_AsLinkedSelf_WithInvalidEmail_Returns400()
    {
        // Arrange — Email violates [EmailAddress]
        var player = await SeedPlayerAsync($"inv-upd-{Guid.NewGuid():N}");
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, player.Id);
        var dto = new UpdatePlayerDto { Email = "not-an-email" };

        // Act
        using var request = UpdateRequest(player.Id, dto, token);
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_WhenLinkedPlayerNoLongerExists_Returns404()
    {
        // Arrange
        var missingId = Guid.NewGuid();
        var token = await OrphanedIdentityTokenAsync(missingId);

        // Act
        using var request = UpdateRequest(missingId, new UpdatePlayerDto { DisplayName = "Ghost" }, token);
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_WithoutToken_Returns401AndLeavesPlayerUnchanged()
    {
        // Arrange
        var player = await SeedPlayerAsync($"anon-upd-{Guid.NewGuid():N}");

        // Act
        using var request = UpdateRequest(player.Id, new UpdatePlayerDto { DisplayName = "Hijacked" }, token: null);
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await FindPlayerAsync(player.Id))!.DisplayName.Should().BeNull();
    }

    [Fact]
    public async Task Update_AsUnlinkedIdentity_Returns403AndLeavesPlayerUnchanged()
    {
        // Arrange
        var player = await SeedPlayerAsync($"unl-upd-{Guid.NewGuid():N}");

        // Act
        using var request = UpdateRequest(
            player.Id,
            new UpdatePlayerDto { DisplayName = "Hijacked" },
            LinkedPlayerTokens.ForSubject(LinkedPlayerTokens.NewSubject()));
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await FindPlayerAsync(player.Id))!.DisplayName.Should().BeNull();
    }

    [Fact]
    public async Task Update_AsLinkedOtherPlayer_Returns403AndLeavesPlayerUnchanged()
    {
        // Arrange
        var victim = await SeedPlayerAsync($"victim-upd-{Guid.NewGuid():N}");
        var attacker = await SeedPlayerAsync($"attacker-upd-{Guid.NewGuid():N}");
        var attackerToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, attacker.Id);

        // Act
        using var request = UpdateRequest(victim.Id, new UpdatePlayerDto { DisplayName = "Hijacked" }, attackerToken);
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await FindPlayerAsync(victim.Id))!.DisplayName.Should().BeNull();
        (await FindPlayerAsync(attacker.Id))!.DisplayName.Should().BeNull();
    }

    [Fact]
    public async Task Update_AsLinkedCaller_ForMissingOtherPlayer_Returns403()
    {
        // Arrange — ownership is checked before existence, so another player's id is never probed.
        var caller = await SeedPlayerAsync($"probe-upd-{Guid.NewGuid():N}");
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, caller.Id);

        // Act
        using var request = UpdateRequest(Guid.NewGuid(), new UpdatePlayerDto { DisplayName = "Ghost" }, token);
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Update_WithNonGuidSubjectLinkedToPlayer_UsesInternalPlayerId()
    {
        // Arrange — the route carries the internal Player.Id; the token carries only the opaque subject.
        var player = await SeedPlayerAsync($"nonguid-upd-{Guid.NewGuid():N}");
        var subject = $"auth0|{Guid.NewGuid():N}";
        await LinkedPlayerTokens.LinkAsync(_factory.Services, player.Id, subject, TeamBuilderWebApplicationFactory.TestIssuer);
        var token = LinkedPlayerTokens.ForSubject(subject);

        // Act
        using var request = UpdateRequest(player.Id, new UpdatePlayerDto { DisplayName = "Opaque" }, token);
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<PlayerDto>())!.Id.Should().Be(player.Id);
        (await FindPlayerAsync(player.Id))!.DisplayName.Should().Be("Opaque");
    }

    [Fact]
    public async Task Update_WithGuidSubjectNamingAnotherPlayer_ComparesRouteToLinkedPlayerId()
    {
        // Arrange — the caller's subject is a GUID equal to another player's id, but the caller
        // is linked to their own player. Only the linked Player.Id is authorized.
        var caller = await SeedPlayerAsync($"guidsub-caller-{Guid.NewGuid():N}");
        var other = await SeedPlayerAsync($"guidsub-other-{Guid.NewGuid():N}");
        var subject = other.Id.ToString();
        await LinkedPlayerTokens.LinkAsync(_factory.Services, caller.Id, subject, TeamBuilderWebApplicationFactory.TestIssuer);
        var token = LinkedPlayerTokens.ForSubject(subject);

        // Act
        using var otherRequest = UpdateRequest(other.Id, new UpdatePlayerDto { DisplayName = "Hijacked" }, token);
        var otherResponse = await _client.SendAsync(otherRequest);
        using var selfRequest = UpdateRequest(caller.Id, new UpdatePlayerDto { DisplayName = "Self" }, token);
        var selfResponse = await _client.SendAsync(selfRequest);

        // Assert
        otherResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await FindPlayerAsync(other.Id))!.DisplayName.Should().BeNull();
        selfResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await FindPlayerAsync(caller.Id))!.DisplayName.Should().Be("Self");
    }

    [Fact]
    public async Task Update_WithGuidSubjectEqualToUnlinkedPlayerId_Returns403()
    {
        // Arrange — no fallback: a GUID subject equal to Player.Id is not a link.
        var player = await SeedPlayerAsync($"nofallback-upd-{Guid.NewGuid():N}");
        var token = LinkedPlayerTokens.ForSubject(player.Id.ToString());

        // Act
        using var request = UpdateRequest(player.Id, new UpdatePlayerDto { DisplayName = "Hijacked" }, token);
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await FindPlayerAsync(player.Id))!.DisplayName.Should().BeNull();
    }

    [Fact]
    public async Task SameSubjectUnderDifferentIssuers_AuthorizesOnlyThatIssuersPlayer()
    {
        // Arrange: clearing Jwt:Issuer disables issuer validation so one host accepts both issuers.
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Issuer"] = "" })));
        var client = factory.CreateClient();
        const string firstIssuer = "https://login.example.com/tenant-a/v2.0";
        const string secondIssuer = "https://login.example.com/tenant-b/v2.0";
        var subject = $"shared|{Guid.NewGuid():N}";
        var firstPlayer = await SeedPlayerAsync($"iss-a-{Guid.NewGuid():N}");
        var secondPlayer = await SeedPlayerAsync($"iss-b-{Guid.NewGuid():N}");
        await LinkedPlayerTokens.LinkAsync(factory.Services, firstPlayer.Id, subject, firstIssuer);
        await LinkedPlayerTokens.LinkAsync(factory.Services, secondPlayer.Id, subject, secondIssuer);
        var secondIssuerToken = LinkedPlayerTokens.ForSubject(subject, secondIssuer);

        // Act
        using var crossUpdate = UpdateRequest(firstPlayer.Id, new UpdatePlayerDto { DisplayName = "Hijacked" }, secondIssuerToken);
        var crossUpdateResponse = await client.SendAsync(crossUpdate);
        using var crossDelete = DeleteRequest(firstPlayer.Id, secondIssuerToken);
        var crossDeleteResponse = await client.SendAsync(crossDelete);
        using var ownUpdate = UpdateRequest(secondPlayer.Id, new UpdatePlayerDto { DisplayName = "Tenant B" }, secondIssuerToken);
        var ownUpdateResponse = await client.SendAsync(ownUpdate);

        // Assert
        crossUpdateResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        crossDeleteResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var first = await FindPlayerAsync(firstPlayer.Id, factory.Services);
        first.Should().NotBeNull();
        first!.DisplayName.Should().BeNull();
        ownUpdateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await FindPlayerAsync(secondPlayer.Id, factory.Services))!.DisplayName.Should().Be("Tenant B");
    }

    // ── DELETE /api/v1/players/{id} ──────────────────────────────────────────

    [Fact]
    public async Task Delete_AsLinkedSelf_Returns204AndRemovesPlayer()
    {
        // Arrange
        var player = await SeedPlayerAsync($"del-{Guid.NewGuid():N}");
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, player.Id);

        // Act
        using var request = DeleteRequest(player.Id, token);
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await FindPlayerAsync(player.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_WhenLinkedPlayerNoLongerExists_Returns404()
    {
        // Arrange
        var missingId = Guid.NewGuid();
        var token = await OrphanedIdentityTokenAsync(missingId);

        // Act
        using var request = DeleteRequest(missingId, token);
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_WithoutToken_Returns401AndLeavesPlayerPresent()
    {
        // Arrange
        var player = await SeedPlayerAsync($"anon-del-{Guid.NewGuid():N}");

        // Act
        using var request = DeleteRequest(player.Id, token: null);
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await FindPlayerAsync(player.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_AsUnlinkedIdentity_Returns403AndLeavesPlayerPresent()
    {
        // Arrange
        var player = await SeedPlayerAsync($"unl-del-{Guid.NewGuid():N}");

        // Act
        using var request = DeleteRequest(player.Id, LinkedPlayerTokens.ForSubject(LinkedPlayerTokens.NewSubject()));
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await FindPlayerAsync(player.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_AsLinkedOtherPlayer_Returns403AndLeavesPlayerPresent()
    {
        // Arrange
        var victim = await SeedPlayerAsync($"victim-del-{Guid.NewGuid():N}");
        var attacker = await SeedPlayerAsync($"attacker-del-{Guid.NewGuid():N}");
        var attackerToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, attacker.Id);

        // Act
        using var request = DeleteRequest(victim.Id, attackerToken);
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await FindPlayerAsync(victim.Id)).Should().NotBeNull();
        (await FindPlayerAsync(attacker.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_WithNonGuidSubjectLinkedToPlayer_DeletesInternalPlayer()
    {
        // Arrange
        var player = await SeedPlayerAsync($"nonguid-del-{Guid.NewGuid():N}");
        var subject = $"auth0|{Guid.NewGuid():N}";
        await LinkedPlayerTokens.LinkAsync(_factory.Services, player.Id, subject, TeamBuilderWebApplicationFactory.TestIssuer);

        // Act
        using var request = DeleteRequest(player.Id, LinkedPlayerTokens.ForSubject(subject));
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await FindPlayerAsync(player.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_WithGuidSubjectNamingAnotherPlayer_Returns403AndLeavesThatPlayerPresent()
    {
        // Arrange — the subject is another player's id; the route is compared to the linked Player.Id.
        var caller = await SeedPlayerAsync($"guidsub-del-caller-{Guid.NewGuid():N}");
        var other = await SeedPlayerAsync($"guidsub-del-other-{Guid.NewGuid():N}");
        var subject = other.Id.ToString();
        await LinkedPlayerTokens.LinkAsync(_factory.Services, caller.Id, subject, TeamBuilderWebApplicationFactory.TestIssuer);

        // Act
        using var request = DeleteRequest(other.Id, LinkedPlayerTokens.ForSubject(subject));
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await FindPlayerAsync(other.Id)).Should().NotBeNull();
        (await FindPlayerAsync(caller.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_WithGuidSubjectEqualToUnlinkedPlayerId_Returns403AndLeavesPlayerPresent()
    {
        // Arrange — no fallback: a GUID subject equal to Player.Id is not a link.
        var player = await SeedPlayerAsync($"nofallback-del-{Guid.NewGuid():N}");

        // Act
        using var request = DeleteRequest(player.Id, LinkedPlayerTokens.ForSubject(player.Id.ToString()));
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await FindPlayerAsync(player.Id)).Should().NotBeNull();
    }
}
