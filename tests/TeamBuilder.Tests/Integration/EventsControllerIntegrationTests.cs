using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Tests.Integration;

public sealed class EventsControllerIntegrationTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly TeamBuilderWebApplicationFactory _factory;

    public EventsControllerIntegrationTests(TeamBuilderWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<TeamEvent> SeedEventAsync(string name = "Test Event", Guid? hostId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

        var ev = new TeamEvent
        {
            Id = Guid.NewGuid(),
            Name = name,
            EventDateUtc = DateTime.UtcNow.AddDays(7),
            Status = EventStatus.Planned,
            MaxParticipants = 32,
            HostId = hostId,
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        };

        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev;
    }

    // ── GET /api/v1/events/{id} ───────────────────────────────────────────────

    [Fact]
    public async Task GetById_WhenEventExists_Returns200()
    {
        // Arrange
        var ev = await SeedEventAsync($"GetById-{Guid.NewGuid():N}");

        // Act
        var response = await _client.GetAsync($"/api/v1/events/{ev.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<EventDto>();
        dto!.Id.Should().Be(ev.Id);
    }

    [Fact]
    public async Task GetById_WhenEventDoesNotExist_Returns404()
    {
        // Act
        var response = await _client.GetAsync($"/api/v1/events/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /api/v1/events ────────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_Returns200WithPaginatedEnvelope()
    {
        // Arrange
        await SeedEventAsync($"List-{Guid.NewGuid():N}");

        // Act
        var response = await _client.GetAsync("/api/v1/events?page=1&pageSize=5");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<PaginatedResult<EventDto>>();
        result.Should().NotBeNull();
        result!.Items.Should().NotBeNull();
    }

    // ── POST /api/v1/events ───────────────────────────────────────────────────

    [Fact]
    public async Task Create_WithValidPayload_Returns201()
    {
        // Arrange
        var dto = new CreateEventDto
        {
            Name = $"Event-{Guid.NewGuid():N}",
            EventDateUtc = DateTime.UtcNow.AddDays(14),
            MaxParticipants = 64
        };
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/events");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<EventDto>();
        created!.Name.Should().Be(dto.Name);
        response.Headers.Location.Should().NotBeNull();
    }

    [Fact]
    public async Task Create_WithoutJwt_Returns401()
    {
        // Arrange
        var dto = new CreateEventDto
        {
            Name = $"Unauth-{Guid.NewGuid():N}",
            EventDateUtc = DateTime.UtcNow.AddDays(14),
            MaxParticipants = 10
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/events", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── PUT /api/v1/events/{id} ───────────────────────────────────────────────

    [Fact]
    public async Task Update_WithValidPayload_Returns200()
    {
        // Arrange
        var hostId = Guid.NewGuid();
        var ev = await SeedEventAsync($"Update-{Guid.NewGuid():N}", hostId);
        var dto = new UpdateEventDto { Name = $"Updated-{Guid.NewGuid():N}" };
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(hostId);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/events/{ev.Id}");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Update_WithoutJwt_Returns401()
    {
        // Arrange
        var ev = await SeedEventAsync($"UpdateUnauth-{Guid.NewGuid():N}");
        var dto = new UpdateEventDto { Name = "Should Fail" };

        // Act
        var response = await _client.PutAsJsonAsync($"/api/v1/events/{ev.Id}", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Update_WhenEventDoesNotExist_Returns404()
    {
        // Arrange
        var dto = new UpdateEventDto { Name = "Ghost Event" };
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/events/{Guid.NewGuid()}");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /api/v1/events/{id} ────────────────────────────────────────────

    [Fact]
    public async Task Delete_WhenEventExists_Returns204()
    {
        // Arrange
        var hostId = Guid.NewGuid();
        var ev = await SeedEventAsync($"Del-{Guid.NewGuid():N}", hostId);
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(hostId);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/events/{ev.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_WithoutJwt_Returns401()
    {
        // Arrange
        var ev = await SeedEventAsync($"DelUnauth-{Guid.NewGuid():N}");

        // Act
        var response = await _client.DeleteAsync($"/api/v1/events/{ev.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Delete_WhenEventDoesNotExist_Returns404()
    {
        // Arrange
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/events/{Guid.NewGuid()}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_ByNonHost_Returns403()
    {
        // Arrange — event seeded with a different host
        var hostId = Guid.NewGuid();
        var ev = await SeedEventAsync($"UpdNonHost-{Guid.NewGuid():N}", hostId);
        var dto = new UpdateEventDto { Name = "Non-host rename" };
        var nonHostToken = TeamBuilderWebApplicationFactory.CreateTestJwt(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/events/{ev.Id}");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", nonHostToken);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Delete_ByNonHost_Returns403()
    {
        // Arrange — event seeded with a different host
        var hostId = Guid.NewGuid();
        var ev = await SeedEventAsync($"DelNonHost-{Guid.NewGuid():N}", hostId);
        var nonHostToken = TeamBuilderWebApplicationFactory.CreateTestJwt(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/events/{ev.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", nonHostToken);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Update_WhenHostIdIsNull_Returns409()
    {
        // Arrange — event seeded without a host (orphaned)
        var ev = await SeedEventAsync($"UpdOrphan-{Guid.NewGuid():N}", hostId: null);
        var dto = new UpdateEventDto { Name = "Orphan Rename" };
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/events/{ev.Id}");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Delete_WhenHostIdIsNull_Returns409()
    {
        // Arrange — event seeded without a host (orphaned)
        var ev = await SeedEventAsync($"DelOrphan-{Guid.NewGuid():N}", hostId: null);
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/events/{ev.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
