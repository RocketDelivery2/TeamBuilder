using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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

    private async Task<RosterImport> SeedRosterImportAsync(
        bool isProcessed = false,
        Guid? importedByUserId = null,
        IServiceProvider? services = null)
    {
        using var scope = (services ?? _factory.Services).CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

        var import = new RosterImport
        {
            Id = Guid.NewGuid(),
            SourceName = $"Source-{Guid.NewGuid():N}",
            SourceType = "CSV",
            RawData = "Name,Role\nplayer1,Tank",
            IsProcessed = isProcessed,
            ProcessingNotes = isProcessed ? "Processed from input row: player1,Tank" : null,
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

    private static async Task<HttpResponseMessage> GetWithTokenAsync(HttpClient client, string url, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private Task<HttpResponseMessage> GetWithTokenAsync(string url, string token)
        => GetWithTokenAsync(_client, url, token);

    // ── GET /api/v1/rosterimports/{id} ───────────────────────────────────────

    [Fact]
    public async Task GetById_WithoutJwt_Returns401()
    {
        var import = await SeedRosterImportAsync(importedByUserId: Guid.NewGuid());

        var response = await _client.GetAsync($"/api/v1/rosterimports/{import.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetById_WithAuthenticatedUnlinkedIdentity_Returns403()
    {
        var import = await SeedRosterImportAsync(importedByUserId: Guid.NewGuid());

        var response = await GetWithTokenAsync($"/api/v1/rosterimports/{import.Id}", CreateUnlinkedIdentityToken());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetById_ByImporter_Returns200WithFullDetail()
    {
        var importer = await CreateLinkedIdentityAsync();
        var import = await SeedRosterImportAsync(importedByUserId: importer.PlayerId);

        var response = await GetWithTokenAsync($"/api/v1/rosterimports/{import.Id}", importer.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<RosterImportDto>();
        dto!.Id.Should().Be(import.Id);
        dto.ImportedByUserId.Should().Be(importer.PlayerId);
        dto.RawData.Should().Be(import.RawData);
    }

    [Fact]
    public async Task GetById_ByDifferentLinkedPlayer_Returns403()
    {
        var import = await SeedRosterImportAsync(importedByUserId: Guid.NewGuid());
        var otherPlayer = await CreateLinkedIdentityAsync();

        var response = await GetWithTokenAsync($"/api/v1/rosterimports/{import.Id}", otherPlayer.Token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(import.RawData);
    }

    [Fact]
    public async Task GetById_WhenImportedByUserIdIsNull_Returns403()
    {
        var import = await SeedRosterImportAsync(importedByUserId: null);
        var caller = await CreateLinkedIdentityAsync();

        var response = await GetWithTokenAsync($"/api/v1/rosterimports/{import.Id}", caller.Token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetById_WhenImportDoesNotExist_Returns404()
    {
        var caller = await CreateLinkedIdentityAsync();

        var response = await GetWithTokenAsync($"/api/v1/rosterimports/{Guid.NewGuid()}", caller.Token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetById_WithGuidSubjectEqualToImporterPlayerIdButNoIdentityLink_Returns403()
    {
        // The player exists and owns the import, but has no PlayerIdentity: sub == Player.Id must not authorize.
        var playerId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
            db.Players.Add(new Player { Id = playerId, Username = $"player-{Guid.NewGuid():N}" });
            await db.SaveChangesAsync();
        }
        var import = await SeedRosterImportAsync(importedByUserId: playerId);

        var response = await GetWithTokenAsync(
            $"/api/v1/rosterimports/{import.Id}",
            TeamBuilderWebApplicationFactory.CreateTestJwt(playerId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── GET /api/v1/rosterimports ────────────────────────────────────────────

    [Fact]
    public async Task GetAll_WithoutJwt_Returns401()
    {
        var response = await _client.GetAsync("/api/v1/rosterimports?page=1&pageSize=5");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetAll_WithAuthenticatedUnlinkedIdentity_Returns403()
    {
        var response = await GetWithTokenAsync("/api/v1/rosterimports", CreateUnlinkedIdentityToken());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetAll_ReturnsOnlyCallersImports()
    {
        var importer = await CreateLinkedIdentityAsync(subject: $"non-guid-subject-{Guid.NewGuid():N}");
        var other = await CreateLinkedIdentityAsync();
        var own1 = await SeedRosterImportAsync(importedByUserId: importer.PlayerId);
        var own2 = await SeedRosterImportAsync(isProcessed: true, importedByUserId: importer.PlayerId);
        var othersImport = await SeedRosterImportAsync(importedByUserId: other.PlayerId);
        var orphan = await SeedRosterImportAsync(importedByUserId: null);

        var response = await GetWithTokenAsync("/api/v1/rosterimports?page=1&pageSize=100", importer.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<PaginatedResult<RosterImportSummaryDto>>();
        result!.TotalCount.Should().Be(2);
        result.Items.Select(i => i.Id).Should().BeEquivalentTo(new[] { own1.Id, own2.Id });
        result.Items.Select(i => i.Id).Should().NotContain(new[] { othersImport.Id, orphan.Id });
    }

    [Fact]
    public async Task GetAll_FiltersByIsProcessedWithinCallersImports()
    {
        var importer = await CreateLinkedIdentityAsync();
        var other = await CreateLinkedIdentityAsync();
        var ownProcessed = await SeedRosterImportAsync(isProcessed: true, importedByUserId: importer.PlayerId);
        var ownUnprocessed = await SeedRosterImportAsync(isProcessed: false, importedByUserId: importer.PlayerId);
        await SeedRosterImportAsync(isProcessed: true, importedByUserId: other.PlayerId);
        await SeedRosterImportAsync(isProcessed: false, importedByUserId: other.PlayerId);

        var processed = await (await GetWithTokenAsync("/api/v1/rosterimports?isProcessed=true", importer.Token))
            .Content.ReadFromJsonAsync<PaginatedResult<RosterImportSummaryDto>>();
        var unprocessed = await (await GetWithTokenAsync("/api/v1/rosterimports?isProcessed=false", importer.Token))
            .Content.ReadFromJsonAsync<PaginatedResult<RosterImportSummaryDto>>();

        processed!.TotalCount.Should().Be(1);
        processed.Items.Select(i => i.Id).Should().Equal(ownProcessed.Id);
        unprocessed!.TotalCount.Should().Be(1);
        unprocessed.Items.Select(i => i.Id).Should().Equal(ownUnprocessed.Id);
    }

    [Fact]
    public async Task GetAll_ScopesToCallerBeforePaging()
    {
        var importer = await CreateLinkedIdentityAsync();
        var other = await CreateLinkedIdentityAsync();
        var ownIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
            ownIds.Add((await SeedRosterImportAsync(importedByUserId: importer.PlayerId)).Id);
        // Newer imports from someone else would fill the first pages if paging ran before scoping.
        for (var i = 0; i < 5; i++)
            await SeedRosterImportAsync(importedByUserId: other.PlayerId);

        var page1 = await (await GetWithTokenAsync("/api/v1/rosterimports?page=1&pageSize=2", importer.Token))
            .Content.ReadFromJsonAsync<PaginatedResult<RosterImportSummaryDto>>();
        var page2 = await (await GetWithTokenAsync("/api/v1/rosterimports?page=2&pageSize=2", importer.Token))
            .Content.ReadFromJsonAsync<PaginatedResult<RosterImportSummaryDto>>();

        page1!.TotalCount.Should().Be(3);
        page1.TotalPages.Should().Be(2);
        page1.Items.Should().HaveCount(2);
        page2!.Items.Should().HaveCount(1);
        page1.Items.Concat(page2.Items).Select(i => i.Id).Should().BeEquivalentTo(ownIds);
    }

    [Fact]
    public async Task GetAll_PayloadOmitsRawDataProcessingNotesAndImporterId()
    {
        var importer = await CreateLinkedIdentityAsync();
        var import = await SeedRosterImportAsync(isProcessed: true, importedByUserId: importer.PlayerId);

        var response = await GetWithTokenAsync("/api/v1/rosterimports", importer.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("id").GetGuid().Should().Be(import.Id);
        item.GetProperty("sourceName").GetString().Should().Be(import.SourceName);
        item.TryGetProperty("rawData", out _).Should().BeFalse();
        item.TryGetProperty("processingNotes", out _).Should().BeFalse();
        item.TryGetProperty("importedByUserId", out _).Should().BeFalse();
        json.Should().NotContain(import.RawData).And.NotContain(import.ProcessingNotes!);
    }

    [Fact]
    public async Task GetAll_WithGuidSubjectEqualToPlayerIdButNoIdentityLink_Returns403()
    {
        var playerId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
            db.Players.Add(new Player { Id = playerId, Username = $"player-{Guid.NewGuid():N}" });
            await db.SaveChangesAsync();
        }
        await SeedRosterImportAsync(importedByUserId: playerId);

        var response = await GetWithTokenAsync("/api/v1/rosterimports", TeamBuilderWebApplicationFactory.CreateTestJwt(playerId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Reads_WithSameSubjectUnderDifferentIssuer_DoNotCrossAuthorize()
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
        var firstPlayersImport = await SeedRosterImportAsync(importedByUserId: firstPlayerId, services: factory.Services);
        var secondPlayersImport = await SeedRosterImportAsync(importedByUserId: secondPlayerId, services: factory.Services);
        var otherIssuerToken = CreateIdentityToken(subject, otherIssuer);

        using var detail = await GetWithTokenAsync(client, $"/api/v1/rosterimports/{firstPlayersImport.Id}", otherIssuerToken);
        using var list = await GetWithTokenAsync(client, "/api/v1/rosterimports?pageSize=100", otherIssuerToken);
        using var unlinkedIssuer = await GetWithTokenAsync(
            client, "/api/v1/rosterimports", CreateIdentityToken(subject, "https://unlinked.example.com"));

        detail.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await list.Content.ReadFromJsonAsync<PaginatedResult<RosterImportSummaryDto>>();
        result!.Items.Select(i => i.Id).Should().Equal(secondPlayersImport.Id);
        unlinkedIssuer.StatusCode.Should().Be(HttpStatusCode.Forbidden);
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
