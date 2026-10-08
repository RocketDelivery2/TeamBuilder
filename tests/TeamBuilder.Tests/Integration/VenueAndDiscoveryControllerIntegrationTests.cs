using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// HTTP contracts of the Venue API, private-venue attachment rules, discovery request
/// validation and the discovery rate limit. Anything that runs the spatial query is covered
/// on real SQL Server (<c>LocalDiscoverySqlServerIntegrationTests</c>); these never reach it.
/// </summary>
public sealed class VenueAndDiscoveryControllerIntegrationTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private static readonly DateTime Wednesday8PmChicagoUtc = new(2026, 10, 15, 1, 0, 0, DateTimeKind.Utc);

    private readonly TeamBuilderWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public VenueAndDiscoveryControllerIntegrationTests(TeamBuilderWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── POST /api/v1/venues ──────────────────────────────────────────────────

    [Fact]
    public async Task CreateVenue_Physical_StoresTheCreatorAndReturnsTheFullVenue()
    {
        var (playerId, token) = await NewLinkedPlayerAsync();

        var response = await SendAsync(HttpMethod.Post, "/api/v1/venues", token, PhysicalVenue(privacy: VenuePrivacyLevel.Public, countryCode: "us"));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var venue = (await response.Content.ReadFromJsonAsync<VenueDto>())!;
        (venue.Name, venue.City, venue.CountryCode, venue.Latitude, venue.Longitude).Should().Be(("Union Park", "Chicago", "US", 41.8849m, -87.6661m));
        (venue.PrivacyLevel, venue.IsAddressMasked, venue.IsMine).Should().Be((VenuePrivacyLevel.Public, false, true));
        response.Headers.Location!.ToString().Should().EndWith($"/api/v1/venues/{venue.Id}");
        playerId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CreateVenue_Virtual_NeedsNoCoordinatesOrTimeZone()
    {
        var (_, token) = await NewLinkedPlayerAsync();

        var response = await SendAsync(HttpMethod.Post, "/api/v1/venues", token, new { name = "Discord voice", venueType = VenueType.Virtual, privacyLevel = VenuePrivacyLevel.Public });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var venue = (await response.Content.ReadFromJsonAsync<VenueDto>())!;
        (venue.Latitude, venue.Longitude, venue.VenueType).Should().Be(((decimal?)null, (decimal?)null, VenueType.Virtual));
    }

    public static TheoryData<string, object> InvalidVenues => new()
    {
        { "latitude above 90", new { name = "x", venueType = VenueType.Outdoor, privacyLevel = VenuePrivacyLevel.Public, latitude = 90.5, longitude = 0, timeZoneId = "UTC" } },
        { "latitude below -90", new { name = "x", venueType = VenueType.Outdoor, privacyLevel = VenuePrivacyLevel.Public, latitude = -91, longitude = 0, timeZoneId = "UTC" } },
        { "longitude above 180", new { name = "x", venueType = VenueType.Outdoor, privacyLevel = VenuePrivacyLevel.Public, latitude = 0, longitude = 180.01, timeZoneId = "UTC" } },
        { "longitude below -180", new { name = "x", venueType = VenueType.Indoor, privacyLevel = VenuePrivacyLevel.Public, latitude = 0, longitude = -200, timeZoneId = "UTC" } },
        { "physical without coordinates", new { name = "x", venueType = VenueType.Indoor, privacyLevel = VenuePrivacyLevel.Public, timeZoneId = "America/Chicago" } },
        { "physical with only latitude", new { name = "x", venueType = VenueType.Indoor, privacyLevel = VenuePrivacyLevel.Public, latitude = 41.0, timeZoneId = "America/Chicago" } },
        { "physical without a time zone", new { name = "x", venueType = VenueType.Outdoor, privacyLevel = VenuePrivacyLevel.Public, latitude = 41.0, longitude = -87.0 } },
        { "a Windows time zone", new { name = "x", venueType = VenueType.Outdoor, privacyLevel = VenuePrivacyLevel.Public, latitude = 41.0, longitude = -87.0, timeZoneId = "Central Standard Time" } },
        { "an unknown time zone", new { name = "x", venueType = VenueType.Outdoor, privacyLevel = VenuePrivacyLevel.Public, latitude = 41.0, longitude = -87.0, timeZoneId = "Mars/Olympus_Mons" } },
        { "a three-letter country", new { name = "x", venueType = VenueType.Outdoor, privacyLevel = VenuePrivacyLevel.Public, latitude = 41.0, longitude = -87.0, timeZoneId = "America/Chicago", countryCode = "USA" } },
        { "virtual with coordinates", new { name = "x", venueType = VenueType.Virtual, privacyLevel = VenuePrivacyLevel.Public, latitude = 0, longitude = 0 } },
        { "no privacy level", new { name = "x", venueType = VenueType.Outdoor, latitude = 41.0, longitude = -87.0, timeZoneId = "America/Chicago" } },
        { "no venue type", new { name = "x", privacyLevel = VenuePrivacyLevel.Public, latitude = 41.0, longitude = -87.0, timeZoneId = "America/Chicago" } },
        { "an unknown privacy level", new { name = "x", venueType = VenueType.Outdoor, privacyLevel = 7, latitude = 41.0, longitude = -87.0, timeZoneId = "America/Chicago" } },
        { "a blank name", new { name = "   ", venueType = VenueType.Outdoor, privacyLevel = VenuePrivacyLevel.Public, latitude = 41.0, longitude = -87.0, timeZoneId = "America/Chicago" } },
    };

    [Theory]
    [MemberData(nameof(InvalidVenues))]
    public async Task CreateVenue_Invalid_Is400(string because, object body)
    {
        var (_, token) = await NewLinkedPlayerAsync();

        var response = await SendAsync(HttpMethod.Post, "/api/v1/venues", token, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because);
    }

    [Fact]
    public async Task CreateVenue_NeedsASignedInLinkedPlayer()
    {
        (await SendAsync(HttpMethod.Post, "/api/v1/venues", null, PhysicalVenue())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var unlinked = TeamBuilderWebApplicationFactory.CreateTestJwt(LinkedPlayerTokens.NewSubject());
        (await SendAsync(HttpMethod.Post, "/api/v1/venues", unlinked, PhysicalVenue())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task VenueApi_HasNoUpdateOrDelete()
    {
        var (_, token) = await NewLinkedPlayerAsync();
        var venue = await CreateVenueAsync(token, PhysicalVenue());

        (await SendAsync(HttpMethod.Put, $"/api/v1/venues/{venue.Id}", token, PhysicalVenue())).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        (await SendAsync(HttpMethod.Delete, $"/api/v1/venues/{venue.Id}", token)).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    // ── GET /api/v1/venues/{id} ──────────────────────────────────────────────

    [Fact]
    public async Task GetVenue_Private_IsMaskedForEveryoneButItsCreator()
    {
        var (_, creator) = await NewLinkedPlayerAsync();
        var (_, other) = await NewLinkedPlayerAsync();
        var created = await CreateVenueAsync(creator, PhysicalVenue(privacy: VenuePrivacyLevel.Private));

        foreach (var token in new[] { null, other })
        {
            var masked = await GetVenueAsync(created.Id, token);
            (masked.IsAddressMasked, masked.AddressLine1, masked.PostalCode, masked.Latitude, masked.Longitude).Should().Be((true, null, null, (decimal?)null, (decimal?)null));
            (masked.Name, masked.City, masked.StateOrProvince).Should().Be(("Union Park", "Chicago", "IL"));
        }

        var mine = await GetVenueAsync(created.Id, creator);
        (mine.IsAddressMasked, mine.AddressLine1, mine.Latitude).Should().Be((false, "1501 W Randolph St", (decimal?)41.8849m));
    }

    [Fact]
    public async Task GetVenue_Public_IsFullForAnonymous_AndUnknownIs404()
    {
        var (_, creator) = await NewLinkedPlayerAsync();
        var created = await CreateVenueAsync(creator, PhysicalVenue());

        var venue = await GetVenueAsync(created.Id, null);
        (venue.IsAddressMasked, venue.AddressLine1, venue.Latitude, venue.IsMine).Should().Be((false, "1501 W Randolph St", (decimal?)41.8849m, false));
        (await SendAsync(HttpMethod.Get, $"/api/v1/venues/{Guid.NewGuid()}", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── attaching a venue to a game ──────────────────────────────────────────

    [Fact]
    public async Task CreateEvent_AtAVenue_RecordsIt_AndTheDetailShowsTheVenue()
    {
        var (_, host) = await NewLinkedPlayerAsync();
        var venue = await CreateVenueAsync(host, PhysicalVenue());

        var created = await SendAsync(HttpMethod.Post, "/api/v1/events", host, GameAt(venue.Id));

        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var game = (await created.Content.ReadFromJsonAsync<EventDto>())!;
        (game.VenueId, game.Location).Should().Be(((Guid?)venue.Id, "Union Park"));
        var detail = (await (await SendAsync(HttpMethod.Get, $"/api/v1/events/{game.Id}/detail", null)).Content.ReadFromJsonAsync<OccurrenceDetailDto>())!;
        detail.Venue!.Should().Match<OccurrenceVenueDto>(v => v.VenueId == venue.Id && !v.IsAddressMasked && v.AddressLine1 == "1501 W Randolph St" && v.DistanceMiles == null);
    }

    [Fact]
    public async Task CreateEvent_AtSomeoneElsesPrivateVenue_Is403_AndNothingIsCreated()
    {
        var (_, owner) = await NewLinkedPlayerAsync();
        var (intruderId, intruder) = await NewLinkedPlayerAsync();
        var venue = await CreateVenueAsync(owner, PhysicalVenue(privacy: VenuePrivacyLevel.Private));

        (await SendAsync(HttpMethod.Post, "/api/v1/events", intruder, GameAt(venue.Id))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/v1/events", owner, GameAt(venue.Id))).StatusCode.Should().Be(HttpStatusCode.Created);

        var page = await (await SendAsync(HttpMethod.Get, "/api/v1/players/me/occurrences?includeHosted=true", intruder)).Content.ReadFromJsonAsync<PlayerOccurrencePageDto>();
        page!.Items.Should().BeEmpty();
        intruderId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CreateEvent_AtSomeoneElsesPublicVenue_IsAllowed_AndAnUnknownVenueIs404()
    {
        var (_, owner) = await NewLinkedPlayerAsync();
        var (_, otherHost) = await NewLinkedPlayerAsync();
        var venue = await CreateVenueAsync(owner, PhysicalVenue());

        (await SendAsync(HttpMethod.Post, "/api/v1/events", otherHost, GameAt(venue.Id))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await SendAsync(HttpMethod.Post, "/api/v1/events", otherHost, GameAt(Guid.NewGuid()))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateSeries_AtSomeoneElsesPrivateVenue_Is403()
    {
        var (_, owner) = await NewLinkedPlayerAsync();
        var (_, intruder) = await NewLinkedPlayerAsync();
        var venue = await CreateVenueAsync(owner, PhysicalVenue(privacy: VenuePrivacyLevel.Private));

        var response = await SendAsync(HttpMethod.Post, "/api/v1/event-series", intruder, new
        {
            name = "Weekly run",
            recurrenceRule = "FREQ=WEEKLY;BYDAY=WE",
            localStartTime = "20:00",
            durationMinutes = 120,
            seriesStartDate = "2026-10-14",
            maxParticipants = 10,
            venueId = venue.Id
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
    }

    // ── GET /api/v1/discover/occurrences validation ──────────────────────────

    [Theory]
    [InlineData("lon=-87.6", "lat")]
    [InlineData("lat=41.8", "lon")]
    [InlineData("lat=90.0001&lon=0", "lat")]
    [InlineData("lat=-91&lon=0", "lat")]
    [InlineData("lat=0&lon=180.5", "lon")]
    [InlineData("lat=0&lon=-180.5", "lon")]
    [InlineData("lat=NaN&lon=0", "lat")]
    [InlineData("lat=0&lon=Infinity", "lon")]
    [InlineData("lat=0&lon=0&radiusMiles=0", "radiusMiles")]
    [InlineData("lat=0&lon=0&radiusMiles=100.5", "radiusMiles")]
    [InlineData("lat=0&lon=0&activity=%25basketball%25", "activity")]
    [InlineData("lat=0&lon=0&pageSize=0", "pageSize")]
    [InlineData("lat=0&lon=0&pageSize=51", "pageSize")]
    [InlineData("lat=0&lon=0&fromUtc=2026-10-14T00:00:00Z&toUtc=2026-10-13T00:00:00Z", "toUtc")]
    [InlineData("lat=0&lon=0&fromUtc=2026-10-01T00:00:00Z&toUtc=2026-10-31T00:00:01Z", "toUtc")]
    public async Task Discover_InvalidParameters_Is400_WithoutEchoingTheSearchPoint(string query, string field)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/v1/discover/occurrences?{query}", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(body);
        problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().Contain(name => string.Equals(name, field, StringComparison.OrdinalIgnoreCase));
        body.Should().NotContain("41.8").And.NotContain("87.6");
    }

    [Fact]
    public async Task Discover_AForeignOrBrokenCursor_Is400()
    {
        (await SendAsync(HttpMethod.Get, "/api/v1/discover/occurrences?lat=0&lon=0&cursor=not-a-cursor", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Discover_IsRateLimitedPerClient_With429AndRetryAfter()
    {
        await using var limited = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:Discovery:PermitLimit"] = "3",
                ["RateLimiting:Discovery:WindowSeconds"] = "3600"
            })));
        using var client = limited.CreateClient();

        // Rejected-as-invalid searches still count, so the limiter needs no database here.
        for (var i = 0; i < 3; i++)
            (await client.GetAsync("/api/v1/discover/occurrences")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var limitedResponse = await client.GetAsync("/api/v1/discover/occurrences");
        limitedResponse.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limitedResponse.Headers.RetryAfter.Should().NotBeNull();

        // Other routes are not limited.
        (await client.GetAsync($"/api/v1/venues/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static object PhysicalVenue(VenuePrivacyLevel privacy = VenuePrivacyLevel.Public, string? countryCode = "US") => new
    {
        name = "Union Park",
        addressLine1 = "1501 W Randolph St",
        city = "Chicago",
        stateOrProvince = "IL",
        postalCode = "60607",
        countryCode,
        latitude = 41.8849m,
        longitude = -87.6661m,
        timeZoneId = "America/Chicago",
        venueType = VenueType.Outdoor,
        privacyLevel = privacy
    };

    private static object GameAt(Guid venueId) => new
    {
        name = "Wednesday Basketball 8 PM",
        category = "basketball",
        eventDateUtc = Wednesday8PmChicagoUtc,
        scheduledEndUtc = Wednesday8PmChicagoUtc.AddHours(2),
        venueId,
        rosterRequirements = new[] { new { roleCode = "participant", requiredCount = 10 } }
    };

    private async Task<VenueDto> CreateVenueAsync(string token, object body)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/venues", token, body);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<VenueDto>())!;
    }

    private async Task<VenueDto> GetVenueAsync(Guid id, string? token)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/v1/venues/{id}", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<VenueDto>())!;
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
