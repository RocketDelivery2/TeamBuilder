using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Tests.Application;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// The TB-GEO-001 acceptance loop exactly as the web client drives it, over HTTP on a real,
/// fully migrated SQL Server: a host creates a public venue and "Wednesday Basketball 8 PM"
/// there (participant x 10, host playing); players discover it within 15 miles, open it and
/// join until READY 10/10; one leaves, discovery shows 9/10 with one open spot, another player
/// discovers and takes it, and READY returns. No Team is involved at any point.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class LocalDiscoveryQaFlowSqlServerIntegrationTests : IAsyncLifetime
{
    private const double HostLat = 41.884900, HostLon = -87.666100;

    // The searcher is about 2 miles east of the court.
    private const string Search = "lat=41.884900&lon=-87.627000&radiusMiles=15&activity=basketball";

    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;
    private SqlServerWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public LocalDiscoveryQaFlowSqlServerIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "discoveryqa");
        await _db.MigrateToAsync();
        _factory = new SqlServerWebApplicationFactory(_db.ConnectionString);
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task DiscoverJoinLeaveRefill_ThroughTheWebClientContract()
    {
        // 1. The host creates a public physical venue (coordinates from the browser).
        var (_, hostToken) = await NewLinkedPlayerAsync();
        var venueResponse = await SendAsync(HttpMethod.Post, "/api/v1/venues", hostToken, new
        {
            name = "Union Park",
            addressLine1 = "1501 W Randolph St",
            city = "Chicago",
            stateOrProvince = "IL",
            countryCode = "US",
            latitude = HostLat,
            longitude = HostLon,
            timeZoneId = "America/Chicago",
            venueType = VenueType.Outdoor,
            privacyLevel = VenuePrivacyLevel.Public
        });
        venueResponse.StatusCode.Should().Be(HttpStatusCode.Created, await venueResponse.Content.ReadAsStringAsync());
        var venue = (await venueResponse.Content.ReadFromJsonAsync<VenueDto>())!;

        // 2-6. "Wednesday Basketball 8 PM", category Basketball, 10 participants, host playing.
        var start = NextWednesday8PmChicagoUtc();
        var created = await SendAsync(HttpMethod.Post, "/api/v1/events", hostToken, new
        {
            name = "Wednesday Basketball 8 PM",
            category = "Basketball",
            eventDateUtc = start,
            scheduledEndUtc = start.AddHours(2),
            venueId = venue.Id,
            rosterRequirements = new[] { new { roleCode = "participant", requiredCount = 10 } },
            hostParticipates = true
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var gameId = (await created.Content.ReadFromJsonAsync<EventDto>())!.Id;

        // 7-9. Player A searches basketball within 15 miles of Wednesday evening and sees it.
        var (_, playerA) = await NewLinkedPlayerAsync();
        var window = $"&fromUtc={Iso(start.AddHours(-3))}&toUtc={Iso(start.AddHours(3))}";
        var hit = (await DiscoverAsync(Search + window, playerA)).Items.Should().ContainSingle().Subject;
        hit.Should().Match<DiscoveredOccurrenceDto>(g => g.OccurrenceId == gameId && g.Name == "Wednesday Basketball 8 PM" && g.TimeZoneId == "America/Chicago");
        hit.Venue.DistanceMiles.Should().BeInRange(1.5, 2.5);
        hit.Roster.Should().BeEquivalentTo(new DiscoveredRosterDto { TotalRequiredCount = 10, TotalSupplyCount = 1, TotalOpenQuantity = 9, IsRosterReady = false, IsFull = false });
        hit.Viewer.Should().BeEquivalentTo(new DiscoveryViewerDto { IsHost = false, IsParticipating = false });
        (await DiscoverAsync(Search + window, hostToken)).Items.Single().Viewer!.IsHost.Should().BeTrue();

        // 10-11. A opens the detail and joins through the roster engine.
        var detail = await DetailAsync(gameId, playerA);
        detail.Venue!.Name.Should().Be("Union Park");
        var requirementId = detail.Requirements.Single().Id;
        (await SendAsync(HttpMethod.Post, $"/api/v1/events/{gameId}/roster/claims", playerA, new { requirementId })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await DiscoverAsync(Search + window, playerA)).Items.Single().Viewer.Should().BeEquivalentTo(
            new DiscoveryViewerDto { IsHost = false, IsParticipating = true, ParticipationStatus = RosterAssignmentStatus.Confirmed });

        // 12. Eight more join: READY 10/10, and it drops out of open-spots-only results.
        var players = new List<(string Token, Guid AssignmentId)>();
        for (var i = 0; i < 8; i++)
        {
            var (_, token) = await NewLinkedPlayerAsync();
            var claim = await SendAsync(HttpMethod.Post, $"/api/v1/events/{gameId}/roster/claims", token, new { requirementId });
            claim.StatusCode.Should().Be(HttpStatusCode.Created);
            players.Add((token, (await claim.Content.ReadFromJsonAsync<RosterAssignmentDto>())!.Id));
        }

        var ready = (await DiscoverAsync(Search + window, null)).Items.Single().Roster;
        (ready.TotalSupplyCount, ready.TotalRequiredCount, ready.TotalOpenQuantity, ready.IsRosterReady, ready.IsFull).Should().Be((10, 10, 0, true, true));
        (await DiscoverAsync(Search + window + "&openOnly=true", null)).Items.Should().BeEmpty();

        // 13. A player leaves: 9/10 and one open spot, in discovery and on the detail.
        (await SendAsync(HttpMethod.Post, $"/api/v1/events/{gameId}/roster/assignments/{players[^1].AssignmentId}/leave", players[^1].Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        var reopened = (await DiscoverAsync(Search + window + "&openOnly=true", null)).Items.Single().Roster;
        (reopened.TotalSupplyCount, reopened.TotalOpenQuantity, reopened.IsRosterReady).Should().Be((9, 1, false));
        (await DetailAsync(gameId, null)).OpenQuantity.Should().Be(1);

        // 14-16. Another player discovers it with open spots only and takes the spot: READY again.
        var (_, replacement) = await NewLinkedPlayerAsync();
        var found = (await DiscoverAsync(Search + window + "&openOnly=true", replacement)).Items.Single();
        found.OccurrenceId.Should().Be(gameId);
        (await SendAsync(HttpMethod.Post, $"/api/v1/events/{gameId}/roster/claims", replacement, new { requirementId })).StatusCode.Should().Be(HttpStatusCode.Created);
        var refilled = (await DiscoverAsync(Search + window, replacement)).Items.Single();
        (refilled.Roster.TotalSupplyCount, refilled.Roster.IsRosterReady, refilled.Viewer!.IsParticipating).Should().Be((10, true, true));

        // No Team entity was needed anywhere.
        await using var verify = _db.CreateContext();
        (await verify.Teams.CountAsync()).Should().Be(0);
        (await verify.TeamMembers.CountAsync()).Should().Be(0);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static DateTime NextWednesday8PmChicagoUtc()
    {
        var chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, chicago).Date;
        var days = ((int)DayOfWeek.Wednesday - (int)today.DayOfWeek + 7) % 7;
        var local = today.AddDays(days == 0 ? 7 : days).AddHours(20);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), chicago);
    }

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private async Task<DiscoveredOccurrencePageDto> DiscoverAsync(string query, string? token)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/v1/discover/occurrences?{query}", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<DiscoveredOccurrencePageDto>())!;
    }

    private async Task<OccurrenceDetailDto> DetailAsync(Guid occurrenceId, string? token)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/v1/events/{occurrenceId}/detail", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<OccurrenceDetailDto>())!;
    }

    private async Task<(Guid PlayerId, string Token)> NewLinkedPlayerAsync()
    {
        var playerId = Guid.NewGuid();
        return (playerId, await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, playerId));
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return _client.SendAsync(request);
    }
}
