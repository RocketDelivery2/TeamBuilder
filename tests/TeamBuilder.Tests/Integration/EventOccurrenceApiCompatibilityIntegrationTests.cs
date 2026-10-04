using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// /api/v1/events now serves EventOccurrence rows. These tests pin the legacy wire contract:
/// clients that only know eventDateUtc/location keep working, and the responses keep those
/// fields alongside the additive occurrence fields. Payloads are raw JSON so the test sees
/// exactly what an old client sends and receives.
/// </summary>
public sealed class EventOccurrenceApiCompatibilityIntegrationTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly TeamBuilderWebApplicationFactory _factory;

    public EventOccurrenceApiCompatibilityIntegrationTests(TeamBuilderWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Post_WithOnlyLegacyFields_CreatesOneOffOccurrence_AndEchoesLegacyFields()
    {
        var hostId = Guid.NewGuid();
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, hostId);

        var response = await SendJsonAsync(HttpMethod.Post, "/api/v1/events", token, """
            {
              "name": "Legacy client pickup",
              "eventDateUtc": "2026-12-01T18:30:00Z",
              "location": "Riverside Park, field 3",
              "maxParticipants": 12
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("eventDateUtc").GetDateTime().Should().Be(new DateTime(2026, 12, 1, 18, 30, 0, DateTimeKind.Utc));
        root.GetProperty("scheduledStartUtc").GetDateTime().Should().Be(root.GetProperty("eventDateUtc").GetDateTime());
        root.GetProperty("location").GetString().Should().Be("Riverside Park, field 3");
        root.GetProperty("scheduledEndUtc").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("seriesId").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("venueId").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("teamId").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("isDetached").GetBoolean().Should().BeFalse();

        var stored = await FindAsync(root.GetProperty("id").GetGuid());
        stored!.ScheduledStartUtc.Should().Be(new DateTime(2026, 12, 1, 18, 30, 0, DateTimeKind.Utc));
        stored.LegacyLocation.Should().Be("Riverside Park, field 3");
        stored.SeriesId.Should().BeNull();
        stored.TeamId.Should().BeNull();
        stored.VenueId.Should().BeNull();
        stored.ScheduledEndUtc.Should().BeNull();
        stored.HostId.Should().Be(hostId);
    }

    [Fact]
    public async Task GetById_ReturnsLegacyFields_EqualToOccurrenceFields()
    {
        var start = new DateTime(2026, 12, 2, 1, 15, 0, DateTimeKind.Utc);
        var occurrence = await SeedAsync(o => { o.ScheduledStartUtc = start; o.LegacyLocation = "Gym B"; });

        var response = await _client.GetAsync($"/api/v1/events/{occurrence.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("eventDateUtc").GetDateTime().Should().Be(start);
        root.GetProperty("scheduledStartUtc").GetDateTime().Should().Be(start);
        root.GetProperty("location").GetString().Should().Be("Gym B");
    }

    [Fact]
    public async Task GetAll_ItemsKeepLegacyFields()
    {
        var category = $"compat-{Guid.NewGuid():N}";
        await SeedAsync(o => { o.Category = category; o.LegacyLocation = "Court 1"; });

        var response = await _client.GetAsync($"/api/v1/events?category={category}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = json.RootElement.GetProperty("items").EnumerateArray().Single();
        item.TryGetProperty("eventDateUtc", out _).Should().BeTrue();
        item.GetProperty("location").GetString().Should().Be("Court 1");
        item.GetProperty("eventDateUtc").GetDateTime().Should().Be(item.GetProperty("scheduledStartUtc").GetDateTime());
    }

    [Fact]
    public async Task Get_WithVenue_ReportsVenueNameAsLocation()
    {
        var venue = new Venue { Id = Guid.NewGuid(), Name = "Northside Rec Center", VenueType = VenueType.Indoor };
        var occurrence = await SeedAsync(o => { o.Venue = venue; o.VenueId = venue.Id; o.LegacyLocation = "old free text"; });

        var response = await _client.GetAsync($"/api/v1/events/{occurrence.Id}");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("location").GetString().Should().Be("Northside Rec Center");
        json.RootElement.GetProperty("venueId").GetGuid().Should().Be(venue.Id);
    }

    [Fact]
    public async Task Put_WithLegacyEventDateUtcAndLocation_UpdatesOccurrenceFields()
    {
        var hostId = Guid.NewGuid();
        var occurrence = await SeedAsync(o => { o.HostId = hostId; o.LegacyLocation = "Before"; });
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, hostId);

        var response = await SendJsonAsync(HttpMethod.Put, $"/api/v1/events/{occurrence.Id}", token, """
            { "eventDateUtc": "2027-01-09T16:00:00Z", "location": "After" }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("eventDateUtc").GetDateTime().Should().Be(new DateTime(2027, 1, 9, 16, 0, 0, DateTimeKind.Utc));
        json.RootElement.GetProperty("location").GetString().Should().Be("After");

        var stored = await FindAsync(occurrence.Id);
        stored!.ScheduledStartUtc.Should().Be(new DateTime(2027, 1, 9, 16, 0, 0, DateTimeKind.Utc));
        stored.LegacyLocation.Should().Be("After");
        stored.ScheduledEndUtc.Should().BeNull();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string url, string token, string json)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private async Task<EventOccurrence> SeedAsync(Action<EventOccurrence> configure)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var occurrence = new EventOccurrence
        {
            Id = Guid.NewGuid(),
            Name = $"Occurrence-{Guid.NewGuid():N}",
            ScheduledStartUtc = DateTime.UtcNow.AddDays(3),
            Status = EventStatus.Planned,
            MaxParticipants = 10
        };
        configure(occurrence);
        db.Events.Add(occurrence);
        await db.SaveChangesAsync();
        return occurrence;
    }

    private async Task<EventOccurrence?> FindAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        return await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);
    }
}
