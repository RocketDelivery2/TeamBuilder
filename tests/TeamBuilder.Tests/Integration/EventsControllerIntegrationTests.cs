using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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

    private async Task<int> GetEventCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        return await db.Events.CountAsync();
    }

    private async Task<(Guid PlayerId, string Subject, string Token)> CreateLinkedIdentityAsync(
        Guid? playerId = null,
        string? subject = null,
        string? issuer = null)
    {
        playerId ??= Guid.NewGuid();
        subject ??= $"external-{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        db.Players.Add(new Player { Id = playerId.Value, Username = $"player-{Guid.NewGuid():N}" });
        db.PlayerIdentities.Add(new PlayerIdentity
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId.Value,
            Issuer = issuer ?? TeamBuilderWebApplicationFactory.TestIssuer,
            Subject = subject,
            Provider = "oidc"
        });
        await db.SaveChangesAsync();

        return (playerId.Value, subject, TeamBuilderWebApplicationFactory.CreateTestJwtWithPlayerClaim(subject));
    }

    private static string CreateUnlinkedIdentityToken()
        => TeamBuilderWebApplicationFactory.CreateTestJwtWithPlayerClaim($"unlinked-{Guid.NewGuid():N}");

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
        var caller = await CreateLinkedIdentityAsync(subject: $"non-guid-subject-{Guid.NewGuid():N}");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/events");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<EventDto>();
        created!.Name.Should().Be(dto.Name);
        created.HostId.Should().Be(caller.PlayerId);
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

    [Fact]
    public async Task Create_WithAuthenticatedUnlinkedIdentity_Returns403AndDoesNotPersistEvent()
    {
        // Arrange
        var before = await GetEventCountAsync();
        var dto = new CreateEventDto
        {
            Name = $"MissingClaim-{Guid.NewGuid():N}",
            EventDateUtc = DateTime.UtcNow.AddDays(14),
            MaxParticipants = 10
        };
        var token = CreateUnlinkedIdentityToken();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/events");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetEventCountAsync()).Should().Be(before);
    }

    // ── PUT /api/v1/events/{id} ───────────────────────────────────────────────

    [Fact]
    public async Task Update_WithValidPayload_Returns200()
    {
        // Arrange
        var hostId = Guid.NewGuid();
        var ev = await SeedEventAsync($"Update-{Guid.NewGuid():N}", hostId);
        var dto = new UpdateEventDto { Name = $"Updated-{Guid.NewGuid():N}" };
        var host = await CreateLinkedIdentityAsync(hostId);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/events/{ev.Id}");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.Token);

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
        var caller = await CreateLinkedIdentityAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/events/{Guid.NewGuid()}");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);

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
        var host = await CreateLinkedIdentityAsync(hostId);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/events/{ev.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.Token);

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
        var caller = await CreateLinkedIdentityAsync();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/events/{Guid.NewGuid()}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);

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
        var nonHost = await CreateLinkedIdentityAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/events/{ev.Id}");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", nonHost.Token);

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
        var nonHost = await CreateLinkedIdentityAsync();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/events/{ev.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", nonHost.Token);

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
        var caller = await CreateLinkedIdentityAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/events/{ev.Id}");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);

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
        var caller = await CreateLinkedIdentityAsync();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/events/{ev.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
