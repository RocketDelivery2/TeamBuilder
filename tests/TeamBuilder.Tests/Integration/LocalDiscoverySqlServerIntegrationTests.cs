using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Tests.Application;
using static TeamBuilder.Tests.Integration.DiscoverySeeding;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// <c>GET /api/v1/discover/occurrences</c> over HTTP on a real, fully migrated SQL Server 2022
/// database, so every distance comes from SQL Server <c>geography</c> (SRID 4326) and the
/// persisted computed <c>Venues.SearchLocation</c>. Covers the 15/25/50-mile bands, the exact
/// boundary, both hemispheres and the antimeridian, exclusions (virtual, no coordinates, no
/// venue, closed, outside the window, other activities), multi-requirement totals, openOnly,
/// keyset paging over equal distances and identical starts, private-venue masking for every
/// kind of caller, and that a search point is neither persisted nor logged.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class LocalDiscoverySqlServerIntegrationTests : IAsyncLifetime
{
    private static readonly DateTime Now = DateTime.UtcNow;

    /// <summary>A start safely inside the default window (now .. now + 7 days).</summary>
    private static readonly DateTime Soon = new DateTime(Now.Year, Now.Month, Now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(2).AddHours(1);

    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;
    private SqlServerWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public LocalDiscoverySqlServerIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "discovery");
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

    // ── radius ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RadiusPresets_IncludeExactlyTheirBands_ClosestFirst()
    {
        var games = new Dictionary<double, Guid>();
        await SeedAsync(db =>
        {
            foreach (var miles in new[] { 60.0, 0.0, 40.0, 10.0, 20.0 })
            {
                var (lat, lon) = North(ChicagoLat, ChicagoLon, miles);
                var venue = Venue(lat, lon);
                db.Venues.Add(venue);
                games[miles] = Game(db, venue, Soon, requirements: ("participant", 10, 0)).Id;
            }
        });

        (await IdsAsync(15)).Should().Equal(games[0], games[10]);
        (await IdsAsync(25)).Should().Equal(games[0], games[10], games[20]);
        (await IdsAsync(50)).Should().Equal(games[0], games[10], games[20], games[40]);
        (await IdsAsync(100)).Should().Equal(games[0], games[10], games[20], games[40], games[60]);

        var page = await DiscoverAsync($"lat={ChicagoLat}&lon={ChicagoLon}&radiusMiles=100");
        page.Items[0].Venue.DistanceMiles.Should().BeLessThan(0.01, "the same point is approximately zero");
        page.Items[1].Venue.DistanceMiles.Should().BeApproximately(10, 0.1);
        page.Items[3].Venue.DistanceMiles.Should().BeApproximately(40, 0.3);

        // Radius defaults to 15 miles.
        (await DiscoverAsync($"lat={ChicagoLat}&lon={ChicagoLon}")).Items.Select(i => i.OccurrenceId).Should().Equal(games[0], games[10]);
    }

    [Fact]
    public async Task Boundary_IsInclusive_AtTheMetre()
    {
        Venue venue = null!;
        Guid gameId = default;
        await SeedAsync(db =>
        {
            var (lat, lon) = North(ChicagoLat, ChicagoLon, 15);
            venue = Venue(lat, lon);
            db.Venues.Add(venue);
            gameId = Game(db, venue, Soon, requirements: ("participant", 10, 0)).Id;
        });
        var meters = await ScalarAsync<double>($"""
            SELECT [SearchLocation].STDistance(geography::Point({ChicagoLat.ToString(CultureInfo.InvariantCulture)}, {ChicagoLon.ToString(CultureInfo.InvariantCulture)}, 4326))
            FROM [Venues] WHERE [Id] = '{venue.Id}'
            """);

        string Radius(double m) => (m / 1609.344).ToString("R", CultureInfo.InvariantCulture);
        (await DiscoverAsync($"lat={ChicagoLat}&lon={ChicagoLon}&radiusMiles={Radius(meters + 1)}")).Items.Select(i => i.OccurrenceId).Should().Equal(gameId);
        (await DiscoverAsync($"lat={ChicagoLat}&lon={ChicagoLon}&radiusMiles={Radius(meters - 1)}")).Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData(-33.868820, 151.209296, -33.900000, 151.250000, "Sydney (south/east)")]
    [InlineData(-34.603722, -58.381592, -34.580000, -58.420000, "Buenos Aires (south/west)")]
    [InlineData(64.128288, -21.827774, 64.140000, -21.900000, "Reykjavik (north/west)")]
    [InlineData(-17.800000, 179.990000, -17.800000, -179.950000, "across the antimeridian")]
    [InlineData(51.477928, -0.010000, 51.477928, 0.010000, "across the prime meridian")]
    public async Task OtherHemispheres_AndTheAntimeridian_AreNearby(double searchLat, double searchLon, double venueLat, double venueLon, string because)
    {
        Guid gameId = default;
        await SeedAsync(db =>
        {
            var near = Venue((decimal)venueLat, (decimal)venueLon);
            var chicago = Venue((decimal)ChicagoLat, (decimal)ChicagoLon);
            db.Venues.AddRange(near, chicago);
            gameId = Game(db, near, Soon, requirements: ("participant", 10, 0)).Id;
            Game(db, chicago, Soon, requirements: ("participant", 10, 0));
        });

        var page = await DiscoverAsync(Query(searchLat, searchLon, 15));

        page.Items.Should().ContainSingle(because).Which.OccurrenceId.Should().Be(gameId);
        page.Items[0].Venue.DistanceMiles.Should().BeLessThan(15);
    }

    // ── exclusions ───────────────────────────────────────────────────────────

    [Fact]
    public async Task OnlyLiveGamesAtPhysicalVenuesWithCoordinates_InTheWindow_ForTheActivity_Match()
    {
        Guid match = default;
        await SeedAsync(db =>
        {
            var court = Venue((decimal)ChicagoLat, (decimal)ChicagoLon);
            // A virtual venue that (impossibly through the API) carries coordinates still has
            // no search location.
            var virtualWithCoordinates = Venue((decimal)ChicagoLat, (decimal)ChicagoLon, type: VenueType.Virtual);
            var noCoordinates = Venue(null, null);
            var latitudeOnly = Venue((decimal)ChicagoLat, null);
            db.Venues.AddRange(court, virtualWithCoordinates, noCoordinates, latitudeOnly);

            match = Game(db, court, Soon, category: "Basketball", requirements: ("participant", 10, 0)).Id;
            Game(db, virtualWithCoordinates, Soon, requirements: ("participant", 10, 0));
            Game(db, noCoordinates, Soon, requirements: ("participant", 10, 0));
            Game(db, latitudeOnly, Soon, requirements: ("participant", 10, 0));
            var legacy = Game(db, venue: null, Soon, requirements: ("participant", 10, 0));
            legacy.LegacyLocation = "Chicago Loop gym";
            foreach (var status in new[] { EventStatus.Cancelled, EventStatus.Completed, EventStatus.Archived })
                Game(db, court, Soon, status: status, requirements: ("participant", 10, 0));
            Game(db, court, Now.AddHours(-1), requirements: ("participant", 10, 0));
            Game(db, court, Now.AddDays(8), requirements: ("participant", 10, 0));
            Game(db, court, Soon, category: "volleyball", name: "Basketball players welcome", requirements: ("participant", 10, 0));
            Game(db, court, Soon, category: null, name: "basketball", requirements: ("participant", 10, 0));
        });

        (await DiscoverAsync($"lat={ChicagoLat}&lon={ChicagoLon}&activity=BASKETBALL")).Items.Select(i => i.OccurrenceId).Should().Equal(match);
        (await DiscoverAsync($"lat={ChicagoLat}&lon={ChicagoLon}&activity=%20basketball%20")).Items.Select(i => i.OccurrenceId).Should().Equal(match);

        // Without an activity every category at the court is listed, still no others.
        (await DiscoverAsync($"lat={ChicagoLat}&lon={ChicagoLon}")).Items.Should().HaveCount(3);

        // A null-island search never finds venues without coordinates.
        (await DiscoverAsync("lat=0&lon=0&radiusMiles=100")).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task TimeWindow_SupportsDateOnly_Evening_AndAround8Pm_InTheVenueZone()
    {
        var chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var wednesday = NextLocal(DayOfWeek.Wednesday, chicago);
        DateTime Utc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), chicago);
        var games = new Dictionary<string, Guid>();
        await SeedAsync(db =>
        {
            var court = Venue((decimal)ChicagoLat, (decimal)ChicagoLon);
            db.Venues.Add(court);
            foreach (var (label, local) in new[] { ("6pm", wednesday.AddHours(18)), ("8pm", wednesday.AddHours(20)), ("10pm", wednesday.AddHours(22)), ("thu8pm", wednesday.AddDays(1).AddHours(20)) })
                games[label] = Game(db, court, Utc(local), requirements: ("participant", 10, 0)).Id;
        });

        async Task<IEnumerable<Guid>> Window(DateTime fromLocal, DateTime toLocal) =>
            (await DiscoverAsync($"lat={ChicagoLat}&lon={ChicagoLon}&fromUtc={Iso(Utc(fromLocal))}&toUtc={Iso(Utc(toLocal))}")).Items.Select(i => i.OccurrenceId);

        (await Window(wednesday, wednesday.AddDays(1))).Should().BeEquivalentTo([games["6pm"], games["8pm"], games["10pm"]], "date-only: the whole local Wednesday");
        (await Window(wednesday.AddHours(17), wednesday.AddHours(23))).Should().BeEquivalentTo([games["6pm"], games["8pm"], games["10pm"]], "evening");
        (await Window(wednesday.AddHours(19), wednesday.AddHours(21))).Should().Equal(games["8pm"]);

        var eight = (await DiscoverAsync($"lat={ChicagoLat}&lon={ChicagoLon}&fromUtc={Iso(Utc(wednesday.AddHours(19)))}&toUtc={Iso(Utc(wednesday.AddHours(21)))}")).Items.Single();
        (eight.TimeZoneId, eight.ScheduledStartUtc.Kind).Should().Be(("America/Chicago", DateTimeKind.Utc));
        TimeZoneInfo.ConvertTimeFromUtc(eight.ScheduledStartUtc, chicago).Hour.Should().Be(20, "the stored instant is shown in the venue's zone");
    }

    // ── roster totals ────────────────────────────────────────────────────────

    [Fact]
    public async Task Totals_SpanEveryRequirement_AndOpenOnlyKeepsGamesWithASpot()
    {
        Guid raid = default, full = default, noRoster = default, open = default;
        await SeedAsync(db =>
        {
            var court = Venue((decimal)ChicagoLat, (decimal)ChicagoLon);
            db.Venues.Add(court);
            raid = Game(db, court, Soon.AddMinutes(1), historyRows: 2, requirements: [("tank", 2, 2), ("healer", 4, 3), ("dps", 14, 14)]).Id;
            full = Game(db, court, Soon.AddMinutes(2), historyRows: 1, requirements: ("participant", 10, 10)).Id;
            noRoster = Game(db, court, Soon.AddMinutes(3)).Id;
            open = Game(db, court, Soon.AddMinutes(4), requirements: ("participant", 10, 7)).Id;
        });

        var all = (await DiscoverAsync($"lat={ChicagoLat}&lon={ChicagoLon}")).Items.ToDictionary(i => i.OccurrenceId, i => i.Roster);

        all[raid].Should().BeEquivalentTo(new DiscoveredRosterDto { TotalRequiredCount = 20, TotalSupplyCount = 19, TotalOpenQuantity = 1, IsRosterReady = false, IsFull = false });
        all[full].Should().BeEquivalentTo(new DiscoveredRosterDto { TotalRequiredCount = 10, TotalSupplyCount = 10, TotalOpenQuantity = 0, IsRosterReady = true, IsFull = true });
        all[noRoster].Should().BeEquivalentTo(new DiscoveredRosterDto { TotalRequiredCount = 0, TotalSupplyCount = 0, TotalOpenQuantity = 0, IsRosterReady = false, IsFull = false });
        all[open].Should().BeEquivalentTo(new DiscoveredRosterDto { TotalRequiredCount = 10, TotalSupplyCount = 7, TotalOpenQuantity = 3, IsRosterReady = false, IsFull = false });

        (await DiscoverAsync($"lat={ChicagoLat}&lon={ChicagoLon}&openOnly=true")).Items.Select(i => i.OccurrenceId).Should().Equal(raid, open);
    }

    // ── paging ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Paging_IsDeterministic_AcrossEqualDistancesAndIdenticalStarts()
    {
        await SeedAsync(db =>
        {
            // Seven games at one venue (identical distance), three of them at the same instant.
            var shared = Venue(North(ChicagoLat, ChicagoLon, 3).Lat, (decimal)ChicagoLon);
            // Two venues mirrored east and west of the search point: equal or near-equal distance.
            var east = Venue((decimal)ChicagoLat, (decimal)(ChicagoLon + 0.05));
            var west = Venue((decimal)ChicagoLat, (decimal)(ChicagoLon - 0.05));
            db.Venues.AddRange(shared, east, west);
            for (var i = 0; i < 7; i++)
                Game(db, shared, i < 3 ? Soon : Soon.AddMinutes(i), requirements: ("participant", 10, i % 3));
            for (var i = 0; i < 3; i++)
            {
                Game(db, east, Soon, requirements: ("participant", 10, 0));
                Game(db, west, Soon, requirements: ("participant", 10, 0));
            }
        });

        var expected = (await DiscoverAsync($"lat={ChicagoLat}&lon={ChicagoLon}&pageSize=50")).Items;
        expected.Should().HaveCount(13);
        expected.Select(i => i.Venue.DistanceMiles).Should().BeInAscendingOrder();

        foreach (var pageSize in new[] { 1, 2, 3, 5 })
        {
            var walked = await WalkAsync($"lat={ChicagoLat}&lon={ChicagoLon}", pageSize);
            walked.Select(i => i.OccurrenceId).Should().Equal(expected.Select(i => i.OccurrenceId), $"pageSize {pageSize}");
        }
    }

    [Fact]
    public async Task Cursor_IsBoundToItsSearch_AndFollowsKeysetSemanticsForNewGames()
    {
        Venue near = null!, far = null!;
        await SeedAsync(db =>
        {
            near = Venue(North(ChicagoLat, ChicagoLon, 1).Lat, (decimal)ChicagoLon);
            far = Venue(North(ChicagoLat, ChicagoLon, 5).Lat, (decimal)ChicagoLon);
            db.Venues.AddRange(near, far);
            Game(db, near, Soon, requirements: ("participant", 10, 0));
            Game(db, near, Soon.AddHours(1), requirements: ("participant", 10, 0));
            Game(db, far, Soon, requirements: ("participant", 10, 0));
        });
        var search = $"lat={ChicagoLat}&lon={ChicagoLon}&pageSize=2";
        var first = await DiscoverAsync(search);
        first.NextCursor.Should().NotBeNull();

        // The cursor cannot be replayed against another point, radius, window or filter.
        foreach (var other in new[] { $"lat={ChicagoLat + 0.01}&lon={ChicagoLon}", $"lat={ChicagoLat}&lon={ChicagoLon}&radiusMiles=25", $"lat={ChicagoLat}&lon={ChicagoLon}&openOnly=true", $"lat={ChicagoLat}&lon={ChicagoLon}&activity=soccer" })
            (await GetAsync($"/api/v1/discover/occurrences?{other}&pageSize=2&cursor={first.NextCursor}")).StatusCode.Should().Be(HttpStatusCode.BadRequest, other);

        // Keyset semantics: a game that sorts before the cursor is not shown on later pages; one
        // that sorts after it is.
        Guid before = default, after = default;
        await SeedAsync(db =>
        {
            before = Game(db, near, Soon.AddMinutes(-30), requirements: ("participant", 10, 0)).Id;
            after = Game(db, far, Soon.AddHours(3), requirements: ("participant", 10, 0)).Id;
        });
        var rest = new List<DiscoveredOccurrenceDto>();
        for (var cursor = first.NextCursor; cursor is not null;)
        {
            var page = await DiscoverAsync($"{search}&cursor={cursor}");
            rest.AddRange(page.Items);
            cursor = page.NextCursor;
        }

        rest.Select(i => i.OccurrenceId).Should().Contain(after).And.NotContain(before).And.NotContain(first.Items.Select(i => i.OccurrenceId));
    }

    // ── private venues and the viewer ────────────────────────────────────────

    [Fact]
    public async Task PrivateVenue_IsMaskedForAnonymousAndUnrelated_FullForHostAndParticipant()
    {
        var (hostId, hostToken) = await NewLinkedPlayerAsync();
        var (participantId, participantToken) = await NewLinkedPlayerAsync();
        var (_, strangerToken) = await NewLinkedPlayerAsync();
        Guid gameId = default, assignmentId = default;
        await SeedAsync(db =>
        {
            var driveway = Venue(41.912345m, -87.654321m, privacy: VenuePrivacyLevel.Private, name: "Sam's driveway", createdBy: hostId);
            db.Venues.Add(driveway);
            var game = Game(db, driveway, Soon, hostId: hostId, requirements: ("participant", 10, 3));
            gameId = game.Id;
            assignmentId = Guid.NewGuid();
            db.RosterAssignments.Add(new RosterAssignment
            {
                Id = assignmentId,
                OccurrenceId = game.Id,
                PlayerId = participantId,
                RequirementId = db.ChangeTracker.Entries<RosterRequirement>().Single(r => r.Entity.OccurrenceId == game.Id).Entity.Id,
                RoleCode = "participant",
                Status = RosterAssignmentStatus.Confirmed,
                Source = RosterAssignmentSource.Player
            });
        });
        var search = $"/api/v1/discover/occurrences?lat={ChicagoLat}&lon={ChicagoLon}";

        foreach (var token in new[] { null, strangerToken })
        {
            var raw = await (await GetAsync(search, token)).Content.ReadAsStringAsync();
            raw.Should().NotContain("100 Secret Ln").And.NotContain("Back gate").And.NotContain("60607").And.NotContain("41.912345").And.NotContain("87.654321");
            var item = (await DiscoverAsync(search["/api/v1/discover/occurrences?".Length..], token)).Items.Single();
            item.Venue.Should().Match<OccurrenceVenueDto>(v => v.IsAddressMasked && v.Name == "Sam's driveway" && v.City == "Chicago" && v.StateOrProvince == "IL"
                && v.AddressLine1 == null && v.AddressLine2 == null && v.PostalCode == null && v.Latitude == null && v.Longitude == null && v.DistanceMiles != null);
            if (token is null)
                item.Viewer.Should().BeNull();
            else
                item.Viewer.Should().BeEquivalentTo(new DiscoveryViewerDto { IsHost = false, IsParticipating = false, ParticipationStatus = null });

            var detail = await DetailAsync(gameId, token);
            detail.Venue!.Should().Match<OccurrenceVenueDto>(v => v.IsAddressMasked && v.AddressLine1 == null && v.Latitude == null);
        }

        var asParticipant = (await DiscoverAsync($"lat={ChicagoLat}&lon={ChicagoLon}", participantToken)).Items.Single();
        asParticipant.Venue.Should().Match<OccurrenceVenueDto>(v => !v.IsAddressMasked && v.AddressLine1 == "100 Secret Ln" && v.Latitude == 41.912345m && v.Longitude == -87.654321m);
        asParticipant.Viewer.Should().BeEquivalentTo(new DiscoveryViewerDto { IsHost = false, IsParticipating = true, ParticipationStatus = RosterAssignmentStatus.Confirmed });
        (await DetailAsync(gameId, participantToken)).Venue!.AddressLine1.Should().Be("100 Secret Ln");

        var asHost = (await DiscoverAsync($"lat={ChicagoLat}&lon={ChicagoLon}", hostToken)).Items.Single();
        asHost.Venue.IsAddressMasked.Should().BeFalse();
        asHost.Viewer.Should().BeEquivalentTo(new DiscoveryViewerDto { IsHost = true, IsParticipating = false, ParticipationStatus = null });
        (await DetailAsync(gameId, hostToken)).Venue!.PostalCode.Should().Be("60607");

        // Leaving ends the entitlement.
        (await SendAsync(HttpMethod.Post, $"/api/v1/events/{gameId}/roster/assignments/{assignmentId}/leave", participantToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await DiscoverAsync($"lat={ChicagoLat}&lon={ChicagoLon}", participantToken)).Items.Single().Venue.IsAddressMasked.Should().BeTrue();
    }

    [Fact]
    public async Task PrivateVenue_DistanceIsMeasuredToItsGridPoint_SoProbingCannotPinpointIt()
    {
        var exact = (Lat: 41.912345m, Lon: -87.654321m);
        await SeedAsync(db =>
        {
            var driveway = Venue(exact.Lat, exact.Lon, privacy: VenuePrivacyLevel.Private);
            db.Venues.Add(driveway);
            Game(db, driveway, Soon, requirements: ("participant", 10, 0));
        });

        // Searching from the exact spot reports the distance to the snapped point (41.91, -87.65),
        // about 0.25 mi, never zero.
        var fromExactSpot = (await DiscoverAsync(Query((double)exact.Lat, (double)exact.Lon, 15))).Items.Single();
        fromExactSpot.Venue.DistanceMiles.Should().BeInRange(0.1, 0.5);
        var fromGridPoint = (await DiscoverAsync(Query(41.91, -87.65, 15))).Items.Single();
        fromGridPoint.Venue.DistanceMiles.Should().Be(0);
    }

    // ── search location privacy ──────────────────────────────────────────────

    [Fact]
    public async Task SearchPoint_IsNeverPersisted_NorLogged()
    {
        var (_, token) = await NewLinkedPlayerAsync();
        await SeedAsync(db =>
        {
            var court = Venue((decimal)ChicagoLat, (decimal)ChicagoLon);
            db.Venues.Add(court);
            Game(db, court, Soon, requirements: ("participant", 10, 0));
        });
        var before = await TableChecksumsAsync();

        var logs = new CapturingLoggerProvider();
        await using var logged = _factory.WithWebHostBuilder(builder => builder.ConfigureLogging(l => l.AddProvider(logs).SetMinimumLevel(LogLevel.Trace)));
        using var client = logged.CreateClient();
        const string secretLat = "41.873217", secretLon = "-87.627719";
        foreach (var bearer in new[] { null, token })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/discover/occurrences?lat={secretLat}&lon={secretLon}&radiusMiles=25&activity=basketball");
            if (bearer is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadFromJsonAsync<DiscoveredOccurrencePageDto>())!.Items.Should().ContainSingle();
        }
        // An invalid search is logged by the exception/validation path without its values too.
        (await client.GetAsync($"/api/v1/discover/occurrences?lat={secretLat}&lon=-187.627719")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await TableChecksumsAsync()).Should().Equal(before, "a search writes nothing anywhere");
        logs.Messages.Should().NotBeEmpty();
        logs.Messages.Should().NotContain(m => m.Contains("41.8732") || m.Contains("87.6277") || m.Contains("187.6277"));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Query(double lat, double lon, double radius) =>
        $"lat={lat.ToString(CultureInfo.InvariantCulture)}&lon={lon.ToString(CultureInfo.InvariantCulture)}&radiusMiles={radius.ToString(CultureInfo.InvariantCulture)}";

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static DateTime NextLocal(DayOfWeek day, TimeZoneInfo zone)
    {
        var today = TimeZoneInfo.ConvertTimeFromUtc(Now, zone).Date;
        var days = ((int)day - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(days == 0 ? 7 : days);
    }

    private async Task<List<Guid>> IdsAsync(double radius) =>
        (await DiscoverAsync(Query(ChicagoLat, ChicagoLon, radius))).Items.Select(i => i.OccurrenceId).ToList();

    private async Task<List<DiscoveredOccurrenceDto>> WalkAsync(string search, int pageSize)
    {
        var all = new List<DiscoveredOccurrenceDto>();
        string? cursor = null;
        do
        {
            var page = await DiscoverAsync($"{search}&pageSize={pageSize}" + (cursor is null ? "" : $"&cursor={cursor}"));
            page.Items.Count.Should().BeLessThanOrEqualTo(pageSize);
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);

        all.Select(i => i.OccurrenceId).Should().OnlyHaveUniqueItems();
        return all;
    }

    private async Task<DiscoveredOccurrencePageDto> DiscoverAsync(string query, string? token = null)
    {
        var response = await GetAsync($"/api/v1/discover/occurrences?{query}", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<DiscoveredOccurrencePageDto>())!;
    }

    private async Task<OccurrenceDetailDto> DetailAsync(Guid occurrenceId, string? token)
    {
        var response = await GetAsync($"/api/v1/events/{occurrenceId}/detail", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<OccurrenceDetailDto>())!;
    }

    private Task<HttpResponseMessage> GetAsync(string url, string? token = null) => SendAsync(HttpMethod.Get, url, token);

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? token)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private async Task<(Guid PlayerId, string Token)> NewLinkedPlayerAsync()
    {
        var playerId = Guid.NewGuid();
        return (playerId, await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, playerId));
    }

    private async Task SeedAsync(Action<TeamBuilderDbContext> seed)
    {
        await using var db = _db.CreateContext();
        seed(db);
        await db.SaveChangesAsync();
    }

    /// <summary>A content checksum of every user table, to prove a read wrote nothing.</summary>
    private async Task<List<string>> TableChecksumsAsync()
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        var tables = new List<string>();
        await using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT [name] FROM sys.tables WHERE [is_ms_shipped] = 0 ORDER BY [name]";
            await using var reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tables.Add(reader.GetString(0));
        }

        var sums = new List<string>();
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            // RowVersion moves on any write, so the count plus the checksum of every rowversion
            // (or all columns where there is none) catches inserts, updates and deletes.
            command.CommandText = $"SELECT CONCAT(COUNT_BIG(*), ':', CHECKSUM_AGG(BINARY_CHECKSUM(*))) FROM [{table}]";
            sums.Add($"{table}={await command.ExecuteScalarAsync()}");
        }

        return sums;
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T), CultureInfo.InvariantCulture);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _messages);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue($"{category}: {formatter(state, exception)} {exception}");
        }
    }
}
