using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Tests.Integration;

public sealed class RosterImportsControllerIntegrationTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly TeamBuilderWebApplicationFactory _factory;

    public RosterImportsControllerIntegrationTests(TeamBuilderWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<RosterImport> SeedRosterImportAsync(bool isProcessed = false, Guid? importedByUserId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

        var import = new RosterImport
        {
            Id = Guid.NewGuid(),
            SourceName = $"Source-{Guid.NewGuid():N}",
            SourceType = "CSV",
            RawData = "Name,Role\nplayer1,Tank",
            IsProcessed = isProcessed,
            ImportedByUserId = importedByUserId,
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        };

        db.RosterImports.Add(import);
        await db.SaveChangesAsync();
        return import;
    }

    private async Task<int> GetRosterImportCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        return await db.RosterImports.CountAsync();
    }

    private async Task<(Guid PlayerId, string Subject, string Token)> CreateLinkedIdentityAsync(
        Guid? playerId = null,
        string? subject = null,
        string? issuer = null)
    {
        playerId ??= Guid.NewGuid();
        subject ??= $"external-{Guid.NewGuid():N}";
        issuer ??= TeamBuilderWebApplicationFactory.TestIssuer;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        db.Players.Add(new Player { Id = playerId.Value, Username = $"player-{Guid.NewGuid():N}" });
        db.PlayerIdentities.Add(new PlayerIdentity
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId.Value,
            Issuer = issuer,
            Subject = subject,
            Provider = "oidc"
        });
        await db.SaveChangesAsync();

        var token = issuer == TeamBuilderWebApplicationFactory.TestIssuer
            ? TeamBuilderWebApplicationFactory.CreateTestJwtWithPlayerClaim(subject)
            : CreateIdentityToken(subject, issuer);
        return (playerId.Value, subject, token);
    }

    private static string CreateIdentityToken(string subject, string issuer)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TeamBuilderWebApplicationFactory.TestSigningKey));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = TeamBuilderWebApplicationFactory.TestAudience,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = subject,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString()
            },
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        });
    }

    private static string CreateUnlinkedIdentityToken()
        => TeamBuilderWebApplicationFactory.CreateTestJwtWithPlayerClaim($"unlinked-{Guid.NewGuid():N}");

    // ── GET /api/v1/rosterimports/{id} ───────────────────────────────────────

    [Fact]
    public async Task GetById_WhenImportExists_Returns200()
    {
        // Arrange
        var import = await SeedRosterImportAsync();

        // Act
        var response = await _client.GetAsync($"/api/v1/rosterimports/{import.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<RosterImportDto>();
        dto!.Id.Should().Be(import.Id);
    }

    [Fact]
    public async Task GetById_WhenImportDoesNotExist_Returns404()
    {
        // Act
        var response = await _client.GetAsync($"/api/v1/rosterimports/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /api/v1/rosterimports ────────────────────────────────────────────

    [Fact]
    public async Task GetAll_Returns200WithPaginatedEnvelope()
    {
        // Arrange
        await SeedRosterImportAsync();

        // Act
        var response = await _client.GetAsync("/api/v1/rosterimports?page=1&pageSize=5");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<PaginatedResult<RosterImportDto>>();
        result.Should().NotBeNull();
        result!.Items.Should().NotBeNull();
    }

    // ── POST /api/v1/rosterimports ────────────────────────────────────────────

    [Fact]
    public async Task Create_WithValidPayload_Returns201()
    {
        // Arrange
        var dto = new CreateRosterImportDto
        {
            SourceName = $"Import-{Guid.NewGuid():N}",
            SourceType = "CSV",
            RawData = "Name,Role\nstriker99,Tank"
        };
        var caller = await CreateLinkedIdentityAsync(subject: $"non-guid-subject-{Guid.NewGuid():N}");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/rosterimports");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<RosterImportDto>();
        created!.SourceName.Should().Be(dto.SourceName);
        created.ImportedByUserId.Should().Be(caller.PlayerId);
        response.Headers.Location.Should().NotBeNull();
    }

    [Fact]
    public async Task Create_WithoutJwt_Returns401()
    {
        // Arrange
        var dto = new CreateRosterImportDto
        {
            SourceName = $"Unauth-{Guid.NewGuid():N}",
            SourceType = "CSV",
            RawData = "Name,Role\nplayer1,Tank"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/rosterimports", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_WithAuthenticatedUnlinkedIdentity_Returns403AndDoesNotPersistImport()
    {
        // Arrange
        var before = await GetRosterImportCountAsync();
        var dto = new CreateRosterImportDto
        {
            SourceName = $"MissingClaim-{Guid.NewGuid():N}",
            SourceType = "CSV",
            RawData = "Name,Role\nplayer1,Tank"
        };
        var token = CreateUnlinkedIdentityToken();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/rosterimports");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetRosterImportCountAsync()).Should().Be(before);
    }

    // ── PUT /api/v1/rosterimports/{id}/process ────────────────────────────────

    [Fact]
    public async Task Process_WithValidImport_Returns200()
    {
        // Arrange
        var importerId = Guid.NewGuid();
        var import = await SeedRosterImportAsync(isProcessed: false, importedByUserId: importerId);
        var importer = await CreateLinkedIdentityAsync(importerId);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/rosterimports/{import.Id}/process");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", importer.Token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<RosterImportDto>();
        dto!.IsProcessed.Should().BeTrue();
    }

    [Fact]
    public async Task Process_WithoutJwt_Returns401()
    {
        // Arrange
        var import = await SeedRosterImportAsync(isProcessed: false);

        // Act
        var response = await _client.PutAsync($"/api/v1/rosterimports/{import.Id}/process", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Process_WhenImportDoesNotExist_Returns404()
    {
        // Arrange
        var caller = await CreateLinkedIdentityAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/rosterimports/{Guid.NewGuid()}/process");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Process_WhenAlreadyProcessed_Returns409()
    {
        // Arrange
        var importerId = Guid.NewGuid();
        var import = await SeedRosterImportAsync(isProcessed: true, importedByUserId: importerId);
        var importer = await CreateLinkedIdentityAsync(importerId);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/rosterimports/{import.Id}/process");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", importer.Token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── DELETE /api/v1/rosterimports/{id} ────────────────────────────────────

    [Fact]
    public async Task Delete_WhenImportExists_Returns204()
    {
        // Arrange
        var importerId = Guid.NewGuid();
        var import = await SeedRosterImportAsync(importedByUserId: importerId);
        var importer = await CreateLinkedIdentityAsync(importerId);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/rosterimports/{import.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", importer.Token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_WithoutJwt_Returns401()
    {
        // Arrange
        var import = await SeedRosterImportAsync();

        // Act
        var response = await _client.DeleteAsync($"/api/v1/rosterimports/{import.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Delete_WhenImportDoesNotExist_Returns404()
    {
        // Arrange
        var caller = await CreateLinkedIdentityAsync();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/rosterimports/{Guid.NewGuid()}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Process_ByNonImporter_Returns403()
    {
        // Arrange — import seeded with a different owner
        var importerId = Guid.NewGuid();
        var import = await SeedRosterImportAsync(isProcessed: false, importedByUserId: importerId);
        var nonImporter = await CreateLinkedIdentityAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/rosterimports/{import.Id}/process");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", nonImporter.Token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Delete_ByNonImporter_Returns403()
    {
        // Arrange — import seeded with a different owner
        var importerId = Guid.NewGuid();
        var import = await SeedRosterImportAsync(importedByUserId: importerId);
        var nonImporter = await CreateLinkedIdentityAsync();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/rosterimports/{import.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", nonImporter.Token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Process_WhenImportedByUserIdIsNull_Returns409()
    {
        // Arrange — import seeded without an importer (orphaned)
        var import = await SeedRosterImportAsync(isProcessed: false, importedByUserId: null);
        var caller = await CreateLinkedIdentityAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/rosterimports/{import.Id}/process");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Delete_WhenImportedByUserIdIsNull_Returns409()
    {
        // Arrange — import seeded without an importer (orphaned)
        var import = await SeedRosterImportAsync(importedByUserId: null);
        var caller = await CreateLinkedIdentityAsync();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/rosterimports/{import.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Create_WithSameSubjectUnderDifferentIssuers_ResolvesMatchingIssuerPlayer()
    {
        const string otherIssuer = "https://accounts.example.com";
        var subject = $"shared-subject-{Guid.NewGuid():N}";
        var firstPlayerId = Guid.NewGuid();
        var secondPlayerId = Guid.NewGuid();

        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Issuer"] = "" })));
        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
            db.Players.AddRange(
                new Player { Id = firstPlayerId, Username = $"player-{Guid.NewGuid():N}" },
                new Player { Id = secondPlayerId, Username = $"player-{Guid.NewGuid():N}" });
            db.PlayerIdentities.AddRange(
                new PlayerIdentity
                {
                    Id = Guid.NewGuid(),
                    PlayerId = firstPlayerId,
                    Issuer = TeamBuilderWebApplicationFactory.TestIssuer,
                    Subject = subject,
                    Provider = "oidc"
                },
                new PlayerIdentity
                {
                    Id = Guid.NewGuid(),
                    PlayerId = secondPlayerId,
                    Issuer = otherIssuer,
                    Subject = subject,
                    Provider = "oidc"
                });
            await db.SaveChangesAsync();
        }

        var dto = new CreateRosterImportDto
        {
            SourceName = $"Import-{Guid.NewGuid():N}",
            SourceType = "CSV",
            RawData = "Name,Role\nplayer1,Tank"
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/rosterimports")
        {
            Content = JsonContent.Create(dto)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateIdentityToken(subject, otherIssuer));

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<RosterImportDto>();
        created!.ImportedByUserId.Should().Be(secondPlayerId);
        created.ImportedByUserId.Should().NotBe(firstPlayerId);
    }
}
