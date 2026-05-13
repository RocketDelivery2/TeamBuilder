using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/rosterimports");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<RosterImportDto>();
        created!.SourceName.Should().Be(dto.SourceName);
        response.Headers.Location.Should().NotBeNull();
    }

    [Fact]
    public async Task Create_WithoutJwt_Returns401()
    {
        // Arrange — no Authorization header
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

    // ── PUT /api/v1/rosterimports/{id}/process ────────────────────────────────

    [Fact]
    public async Task Process_WithValidImport_Returns200()
    {
        // Arrange
        var importerId = Guid.NewGuid();
        var import = await SeedRosterImportAsync(isProcessed: false, importedByUserId: importerId);
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(importerId);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/rosterimports/{import.Id}/process");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

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
        // Arrange — no Authorization header
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
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/rosterimports/{Guid.NewGuid()}/process");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

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
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(importerId);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/rosterimports/{import.Id}/process");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

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
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(importerId);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/rosterimports/{import.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_WithoutJwt_Returns401()
    {
        // Arrange — no Authorization header
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
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/rosterimports/{Guid.NewGuid()}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

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
        var nonImporterToken = TeamBuilderWebApplicationFactory.CreateTestJwt(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/rosterimports/{import.Id}/process");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", nonImporterToken);

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
        var nonImporterToken = TeamBuilderWebApplicationFactory.CreateTestJwt(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/rosterimports/{import.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", nonImporterToken);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
