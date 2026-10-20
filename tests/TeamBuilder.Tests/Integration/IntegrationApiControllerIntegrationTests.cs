using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// The public integration API over HTTP: players manage their own API keys under
/// /api/v1/players/me/api-keys, and a key reads only its owner's hosted games under
/// /api/integration/v1 (teambuilder.roster.v1). Keys and user tokens never cross over.
/// </summary>
public sealed class IntegrationApiControllerIntegrationTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private const string KeysUrl = "/api/v1/players/me/api-keys";
    private const string ApiUrl = "/api/integration/v1";

    private readonly TeamBuilderWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public IntegrationApiControllerIntegrationTests(TeamBuilderWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── key management ───────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ReturnsTheKeyOnce_AndStoresOnlyItsHash()
    {
        var (playerId, token) = await NewPlayerAsync();

        var response = await SendAsync(HttpMethod.Post, KeysUrl, token, new { name = "  League website  ", expiresInDays = 30 });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var created = (await response.Content.ReadFromJsonAsync<CreatedIntegrationApiKeyDto>())!;
        created.Name.Should().Be("League website");
        created.Key.Should().MatchRegex("^tbk_[0-9a-f]{12}_[A-Za-z0-9_-]{43}$");
        created.KeyPrefix.Should().Be(created.Key[..16]);
        created.Scopes.Should().Equal(IntegrationScopes.RostersRead);
        created.IsActive.Should().BeTrue();
        created.ExpiresAtUtc.Should().BeCloseTo(DateTime.UtcNow.AddDays(30), TimeSpan.FromMinutes(1));

        var listBody = await (await SendAsync(HttpMethod.Get, KeysUrl, token)).Content.ReadAsStringAsync();
        listBody.Should().NotContain(created.Key).And.NotContain(created.Key[17..]);
        listBody.Should().NotContainEquivalentOf("\"key\"");
        var listed = JsonSerializer.Deserialize<List<IntegrationApiKeyDto>>(listBody, JsonSerializerOptions.Web)!;
        listed.Should().ContainSingle().Which.KeyPrefix.Should().Be(created.KeyPrefix);

        await using var scope = _factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>().IntegrationApiKeys.SingleAsync(k => k.PlayerId == playerId);
        stored.SecretHash.Should().Equal(IntegrationApiKeyFormat.Hash(created.Key));
        stored.KeyPrefix.Should().Be(created.Key[4..16]);
    }

    [Fact]
    public async Task Management_NeedsALinkedPlayer()
    {
        (await SendAsync(HttpMethod.Get, KeysUrl, token: null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Post, KeysUrl, token: null, new { name = "x" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var unlinked = LinkedPlayerTokens.ForSubject(LinkedPlayerTokens.NewSubject());
        (await SendAsync(HttpMethod.Get, KeysUrl, unlinked)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, KeysUrl, unlinked, new { name = "x" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":\"   \"}")]
    [InlineData("{\"name\":\"ok\",\"expiresInDays\":0}")]
    [InlineData("{\"name\":\"ok\",\"expiresInDays\":366}")]
    [InlineData("{\"name\":\"ok\",\"scopes\":[]}")]
    [InlineData("{\"name\":\"ok\",\"scopes\":[\"rosters:write\"]}")]
    public async Task Create_RejectsInvalidRequests(string body)
    {
        var (_, token) = await NewPlayerAsync();

        var response = await SendRawAsync(HttpMethod.Post, KeysUrl, token, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await (await SendAsync(HttpMethod.Get, KeysUrl, token)).Content.ReadFromJsonAsync<List<IntegrationApiKeyDto>>()).Should().BeEmpty();
    }

    [Fact]
    public async Task Create_StopsAtTenActiveKeys_UntilOneIsRevoked()
    {
        var (_, token) = await NewPlayerAsync();
        var keys = new List<CreatedIntegrationApiKeyDto>();
        for (var i = 0; i < 10; i++)
            keys.Add(await CreateKeyAsync(token, $"key {i}"));

        var refused = await SendAsync(HttpMethod.Post, KeysUrl, token, new { name = "eleventh" });
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("ApiKeyLimitReached");

        (await SendAsync(HttpMethod.Delete, $"{KeysUrl}/{keys[0].Id}", token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SendAsync(HttpMethod.Post, KeysUrl, token, new { name = "eleventh" })).StatusCode.Should().Be(HttpStatusCode.Created);

        var listed = (await (await SendAsync(HttpMethod.Get, KeysUrl, token)).Content.ReadFromJsonAsync<List<IntegrationApiKeyDto>>())!;
        listed.Should().HaveCount(11);
        listed.Last().Id.Should().Be(keys[0].Id, "revoked keys are listed after active ones");
        listed.Last().IsActive.Should().BeFalse();
        listed.Last().RevokedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Revoke_IsOwnerOnly_AndIdempotent_AndTheKeyStopsWorkingAtOnce()
    {
        var (_, alice) = await NewPlayerAsync();
        var (_, bob) = await NewPlayerAsync();
        var key = await CreateKeyAsync(alice);
        (await CallAsync(key.Key, $"{ApiUrl}/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await SendAsync(HttpMethod.Delete, $"{KeysUrl}/{key.Id}", bob)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CallAsync(key.Key, $"{ApiUrl}/me")).StatusCode.Should().Be(HttpStatusCode.OK, "another player cannot revoke it");

        (await SendAsync(HttpMethod.Delete, $"{KeysUrl}/{key.Id}", alice)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SendAsync(HttpMethod.Delete, $"{KeysUrl}/{key.Id}", alice)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SendAsync(HttpMethod.Delete, $"{KeysUrl}/{Guid.NewGuid()}", alice)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await CallAsync(key.Key, $"{ApiUrl}/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── integration authentication ───────────────────────────────────────────

    [Fact]
    public async Task IntegrationApi_RejectsMissingMalformedUnknownAndExpiredKeys()
    {
        var (playerId, token) = await NewPlayerAsync();
        var valid = await CreateKeyAsync(token);

        var missing = await CallAsync(apiKey: null, $"{ApiUrl}/me");
        missing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        missing.Headers.WwwAuthenticate.ToString().Should().Contain("ApiKey").And.Contain("X-Api-Key");
        (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString().Should().Contain("X-Api-Key");

        foreach (var bad in new[]
                 {
                     "nonsense",
                     valid.Key.ToUpperInvariant(),
                     valid.Key[..^1],
                     valid.Key[..^1] + (valid.Key[^1] == 'A' ? 'B' : 'A'),
                     IntegrationApiKeyFormat.Generate().Key
                 })
        {
            var response = await CallAsync(bad, $"{ApiUrl}/me");
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, bad);
            (await response.Content.ReadAsStringAsync()).Should().NotContain(bad);
        }

        var expired = await SeedKeyAsync(playerId, expiresAtUtc: DateTime.UtcNow.AddMinutes(-1));
        (await CallAsync(expired, $"{ApiUrl}/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CallAsync(valid.Key, $"{ApiUrl}/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UserTokensAndApiKeys_NeverCrossOver()
    {
        var (_, token) = await NewPlayerAsync();
        var key = await CreateKeyAsync(token);

        using (var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiUrl}/me"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a user token is not an integration credential");
        }

        (await CallAsync(key.Key, KeysUrl)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a key cannot manage keys");
        (await CallAsync(key.Key, "/api/v1/players/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a key is not a user session");
    }

    [Fact]
    public async Task KeyWithoutTheRostersReadScope_IsForbidden()
    {
        var (playerId, _) = await NewPlayerAsync();
        var key = await SeedKeyAsync(playerId, scopes: "");

        var response = await CallAsync(key, $"{ApiUrl}/occurrences");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString().Should().Contain("scope");
    }

    [Fact]
    public async Task Me_DescribesTheKeyAndItsOwner_AndRecordsLastUse()
    {
        var (playerId, token) = await NewPlayerAsync(displayName: "Coach Dana");
        var key = await CreateKeyAsync(token, "Scoreboard");
        key.LastUsedAtUtc.Should().BeNull();

        var me = (await (await CallAsync(key.Key, $"{ApiUrl}/me")).Content.ReadFromJsonAsync<IntegrationCallerDto>())!;

        me.KeyId.Should().Be(key.Id);
        me.KeyName.Should().Be("Scoreboard");
        me.PlayerId.Should().Be(playerId);
        me.DisplayName.Should().Be("Coach Dana");
        me.Scopes.Should().Equal(IntegrationScopes.RostersRead);
        var listed = (await (await SendAsync(HttpMethod.Get, KeysUrl, token)).Content.ReadFromJsonAsync<List<IntegrationApiKeyDto>>())!;
        listed.Single().LastUsedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    // ── roster contract ──────────────────────────────────────────────────────

    [Fact]
    public async Task Roster_ExportsTheHostedGame_InTheV1Contract()
    {
        var (hostId, token) = await NewPlayerAsync();
        var key = await CreateKeyAsync(token);
        var game = await SeedGameAsync(hostId, "Wednesday Basketball", DateTime.UtcNow.AddDays(2), privateVenue: true);
        var guard = await SeedRequirementAsync(game.Id, "guard", 2, "Guard", "Point Guard");
        var forward = await SeedRequirementAsync(game.Id, "forward", 2);
        var sam = await SeedPlayerAsync("sam", "Sam R.", email: "sam@example.test");
        var pat = await SeedPlayerAsync("pat", null);
        var lee = await SeedPlayerAsync("lee", "Lee");
        await SeedAssignmentAsync(game.Id, sam, guard.Id, "guard", RosterAssignmentStatus.Confirmed, RosterAssignmentSource.Player, minutesAgo: 30);
        await SeedAssignmentAsync(game.Id, pat, forward.Id, "forward", RosterAssignmentStatus.CheckedIn, RosterAssignmentSource.Host, minutesAgo: 20);
        await SeedAssignmentAsync(game.Id, lee, guard.Id, "guard", RosterAssignmentStatus.Departed, RosterAssignmentSource.Player, minutesAgo: 10);

        var response = await CallAsync(key.Key, $"{ApiUrl}/occurrences/{game.Id}/roster");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().NotContain("sam@example.test", "participants carry public profile fields only");
        json.Should().NotContain("\"email\"");
        var roster = JsonSerializer.Deserialize<IntegrationRosterDto>(json, JsonSerializerOptions.Web)!;
        roster.Schema.Should().Be("teambuilder.roster.v1");
        roster.Occurrence.Id.Should().Be(game.Id);
        roster.Occurrence.Name.Should().Be("Wednesday Basketball");
        roster.Occurrence.ActivityCode.Should().Be("basketball");
        roster.Occurrence.Status.Should().Be("open");
        roster.Occurrence.TimeZoneId.Should().Be("America/New_York");
        (roster.Occurrence.RequiredCount, roster.Occurrence.SupplyCount, roster.Occurrence.OpenQuantity, roster.Occurrence.IsRosterReady)
            .Should().Be((4, 2, 2, false));

        roster.Venue.Should().NotBeNull();
        roster.Venue!.AddressLine1.Should().Be("100 Example Ave", "the key acts for the host, who sees the full venue");
        roster.Venue.Latitude.Should().Be(47.61m);
        roster.Venue.Privacy.Should().Be("private");
        roster.Venue.Type.Should().Be("indoor");

        roster.Requirements.Select(r => (r.RoleCode, r.DisplayPosition, r.SourceRoleLabel, r.RequiredCount, r.SupplyCount, r.OpenQuantity))
            .Should().Equal(("guard", "Guard", "Point Guard", 2, 1, 1), ("forward", null, null, 2, 1, 1));

        roster.Participants.Select(p => (p.PlayerId, p.Username, p.DisplayName, p.RoleCode, p.Status, p.Source))
            .Should().Equal(
                (sam, "sam", "Sam R.", "guard", "confirmed", "player"),
                (pat, "pat", null, "forward", "checked_in", "host"));
        roster.Participants.Should().OnlyContain(p => p.JoinedAtUtc.Kind == DateTimeKind.Utc);
        roster.GeneratedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Roster_OfAGameSomeoneElseHosts_IsNotFound_LikeAMissingOne()
    {
        var (_, token) = await NewPlayerAsync();
        var key = await CreateKeyAsync(token);
        var otherHost = await SeedPlayerAsync($"other{Guid.NewGuid():N}", null);
        var theirs = await SeedGameAsync(otherHost, "Not yours", DateTime.UtcNow.AddDays(1));
        var unhosted = await SeedGameAsync(hostId: null, "Orphan", DateTime.UtcNow.AddDays(1));

        var foreign = await CallAsync(key.Key, $"{ApiUrl}/occurrences/{theirs.Id}/roster");
        var missing = await CallAsync(key.Key, $"{ApiUrl}/occurrences/{Guid.NewGuid()}/roster");

        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CallAsync(key.Key, $"{ApiUrl}/occurrences/{unhosted.Id}/roster")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var foreignProblem = await foreign.Content.ReadFromJsonAsync<JsonElement>();
        var missingProblem = await missing.Content.ReadFromJsonAsync<JsonElement>();
        foreignProblem.GetProperty("title").GetString().Should().Be(missingProblem.GetProperty("title").GetString());
        foreignProblem.TryGetProperty("detail", out _).Should().Be(missingProblem.TryGetProperty("detail", out _));
    }

    [Fact]
    public async Task Occurrences_ListsOnlyTheOwnersGames_InTheWindow_EarliestFirst()
    {
        var (hostId, token) = await NewPlayerAsync();
        var key = await CreateKeyAsync(token);
        var start = new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var late = await SeedGameAsync(hostId, "Late", start.AddDays(5));
        var early = await SeedGameAsync(hostId, "Early", start.AddDays(1));
        await SeedRequirementAsync(early.Id, "participant", 3);
        await SeedGameAsync(hostId, "Before window", start.AddDays(-1));
        await SeedGameAsync(hostId, "At window end", start.AddDays(10));
        await SeedGameAsync(await SeedPlayerAsync($"x{Guid.NewGuid():N}", null), "Someone else's", start.AddDays(2));

        var response = await CallAsync(key.Key, $"{ApiUrl}/occurrences?from=2027-03-01T00:00:00Z&to=2027-03-11T00:00:00Z");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = (await response.Content.ReadFromJsonAsync<PaginatedResult<IntegrationOccurrenceDto>>())!;
        page.TotalCount.Should().Be(2);
        page.Items.Select(o => o.Id).Should().Equal(early.Id, late.Id);
        page.Items.First().Should().BeEquivalentTo(new { RequiredCount = 3, SupplyCount = 0, OpenQuantity = 3, IsRosterReady = false, Status = "open" });

        var second = (await (await CallAsync(key.Key, $"{ApiUrl}/occurrences?from=2027-03-01T00:00:00Z&to=2027-03-11T00:00:00Z&page=2&pageSize=1")).Content
            .ReadFromJsonAsync<PaginatedResult<IntegrationOccurrenceDto>>())!;
        second.Items.Select(o => o.Id).Should().Equal(late.Id);
        second.HasNextPage.Should().BeFalse();
    }

    [Theory]
    [InlineData("from=2027-03-02T00:00:00Z&to=2027-03-01T00:00:00Z", "to")]
    [InlineData("from=2027-01-01T00:00:00Z&to=2027-06-01T00:00:00Z", "to")]
    [InlineData("pageSize=101", "pageSize")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("page=0", "page")]
    [InlineData("page=2147483647&pageSize=100", "page")]
    public async Task Occurrences_RejectsAnInvalidWindowOrPage(string query, string field)
    {
        var (_, token) = await NewPlayerAsync();
        var key = await CreateKeyAsync(token);

        var response = await CallAsync(key.Key, $"{ApiUrl}/occurrences?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue();
    }

    [Fact]
    public async Task IntegrationApi_IsRateLimitedPerKey()
    {
        using var factory = new ConfiguredFactory(new() { ["RateLimiting:Integration:PermitLimit"] = "2" });
        using var client = factory.CreateClient();
        var (_, token) = await NewPlayerAsync(factory);
        var first = await CreateKeyAsync(token, "first", client);
        var second = await CreateKeyAsync(token, "second", client);

        (await CallAsync(first.Key, $"{ApiUrl}/me", client)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CallAsync(first.Key, $"{ApiUrl}/occurrences", client)).StatusCode.Should().Be(HttpStatusCode.OK);
        var limited = await CallAsync(first.Key, $"{ApiUrl}/me", client);

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.Should().NotBeNull();
        (await limited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString().Should().Contain("API key");
        (await CallAsync(second.Key, $"{ApiUrl}/me", client)).StatusCode.Should().Be(HttpStatusCode.OK, "each key has its own budget");
    }

    [Fact]
    public async Task RejectedKeys_AreLimitedPerClient_BeforeAnyLookup_ButValidKeysAreNot()
    {
        using var factory = new ConfiguredFactory(new() { ["RateLimiting:IntegrationAuthFailures:PermitLimit"] = "2" });
        using var client = factory.CreateClient();
        var (_, token) = await NewPlayerAsync(factory);
        var valid = await CreateKeyAsync(token, "valid", client);

        for (var i = 0; i < 3; i++)
            (await CallAsync(valid.Key, $"{ApiUrl}/me", client)).StatusCode.Should().Be(HttpStatusCode.OK, "a valid key spends nothing");
        (await CallAsync(IntegrationApiKeyFormat.Generate().Key, $"{ApiUrl}/me", client)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CallAsync("nonsense", $"{ApiUrl}/me", client)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var throttled = await CallAsync(IntegrationApiKeyFormat.Generate().Key, $"{ApiUrl}/me", client);
        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter.Should().NotBeNull();
        (await CallAsync(valid.Key, $"{ApiUrl}/me", client)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the client is throttled until the window ends");
        (await CallAsync(apiKey: null, $"{ApiUrl}/me", client)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "no key means no lookup to protect");
    }

    [Fact]
    public async Task KeyChanges_AreRateLimitedPerCaller()
    {
        using var factory = new ConfiguredFactory(new() { ["RateLimiting:ApiKeys:PermitLimit"] = "2" });
        using var client = factory.CreateClient();
        var (_, token) = await NewPlayerAsync(factory);

        var key = await CreateKeyAsync(token, "one", client);
        (await SendAsync(HttpMethod.Delete, $"{KeysUrl}/{key.Id}", token, client: client)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var limited = await SendAsync(HttpMethod.Post, KeysUrl, token, new { name = "three" }, client);

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await SendAsync(HttpMethod.Get, KeysUrl, token, client: client)).StatusCode.Should().Be(HttpStatusCode.OK, "listing is not limited");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private Task<(Guid PlayerId, string Token)> NewPlayerAsync(string? displayName = null) => NewPlayerAsync(_factory, displayName);

    private static async Task<(Guid PlayerId, string Token)> NewPlayerAsync(TeamBuilderWebApplicationFactory factory, string? displayName = null)
    {
        var playerId = Guid.NewGuid();
        var token = await LinkedPlayerTokens.ForPlayerAsync(factory.Services, playerId);
        if (displayName is not null)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
            (await db.Players.SingleAsync(p => p.Id == playerId)).DisplayName = displayName;
            await db.SaveChangesAsync();
        }
        return (playerId, token);
    }

    private async Task<CreatedIntegrationApiKeyDto> CreateKeyAsync(string token, string name = "Test key", HttpClient? client = null)
    {
        var response = await SendAsync(HttpMethod.Post, KeysUrl, token, new { name }, client);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<CreatedIntegrationApiKeyDto>())!;
    }

    /// <summary>Inserts a key directly, for states the API cannot produce (expired, scope-less).</summary>
    private async Task<string> SeedKeyAsync(Guid playerId, DateTime? expiresAtUtc = null, string scopes = IntegrationScopes.RostersRead)
    {
        var (key, prefix) = IntegrationApiKeyFormat.Generate();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        db.IntegrationApiKeys.Add(new IntegrationApiKey
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            Name = "seeded",
            KeyPrefix = prefix,
            SecretHash = IntegrationApiKeyFormat.Hash(key),
            Scopes = scopes,
            ExpiresAtUtc = expiresAtUtc,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-2),
            RowVersion = []
        });
        await db.SaveChangesAsync();
        return key;
    }

    private async Task<Guid> SeedPlayerAsync(string username, string? displayName, string? email = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = username,
            DisplayName = displayName,
            Email = email,
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        };
        db.Players.Add(player);
        await db.SaveChangesAsync();
        return player.Id;
    }

    private async Task<EventOccurrence> SeedGameAsync(Guid? hostId, string name, DateTime startsAtUtc, bool privateVenue = false)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        Venue? venue = null;
        if (privateVenue)
        {
            venue = new Venue
            {
                Id = Guid.NewGuid(),
                Name = "North Gym",
                AddressLine1 = "100 Example Ave",
                City = "Seattle",
                StateOrProvince = "WA",
                PostalCode = "98101",
                CountryCode = "US",
                Latitude = 47.61m,
                Longitude = -122.33m,
                TimeZoneId = "America/New_York",
                VenueType = VenueType.Indoor,
                PrivacyLevel = VenuePrivacyLevel.Private,
                CreatedByPlayerId = hostId,
                CreatedAtUtc = DateTime.UtcNow,
                RowVersion = []
            };
            db.Venues.Add(venue);
        }
        var game = new EventOccurrence
        {
            Id = Guid.NewGuid(),
            Name = name,
            Category = "basketball",
            ScheduledStartUtc = startsAtUtc,
            ScheduledEndUtc = startsAtUtc.AddHours(2),
            Status = EventStatus.Open,
            MaxParticipants = 10,
            HostId = hostId,
            VenueId = venue?.Id,
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        };
        db.Events.Add(game);
        await db.SaveChangesAsync();
        return game;
    }

    private async Task<RosterRequirement> SeedRequirementAsync(Guid occurrenceId, string roleCode, int requiredCount, string? displayPosition = null, string? sourceRoleLabel = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var requirement = new RosterRequirement
        {
            Id = Guid.NewGuid(),
            OccurrenceId = occurrenceId,
            RoleCode = roleCode,
            RequiredCount = requiredCount,
            DisplayPosition = displayPosition,
            SourceRoleLabel = sourceRoleLabel,
            CreatedAtUtc = DateTime.UtcNow.AddSeconds(db.RosterRequirements.Count(r => r.OccurrenceId == occurrenceId)),
            RowVersion = []
        };
        db.RosterRequirements.Add(requirement);
        await db.SaveChangesAsync();
        return requirement;
    }

    private async Task SeedAssignmentAsync(Guid occurrenceId, Guid playerId, Guid requirementId, string roleCode, RosterAssignmentStatus status, RosterAssignmentSource source, int minutesAgo)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var at = DateTime.UtcNow.AddMinutes(-minutesAgo);
        db.RosterAssignments.Add(new RosterAssignment
        {
            Id = Guid.NewGuid(),
            OccurrenceId = occurrenceId,
            PlayerId = playerId,
            RequirementId = requirementId,
            RoleCode = roleCode,
            Status = status,
            Source = source,
            ReservedAtUtc = at,
            ConfirmedAtUtc = status is RosterAssignmentStatus.Reserved ? null : at,
            CheckedInAtUtc = status is RosterAssignmentStatus.CheckedIn ? at : null,
            DepartedAtUtc = status is RosterAssignmentStatus.Departed ? at : null,
            CreatedAtUtc = at,
            RowVersion = []
        });
        await db.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> CallAsync(string? apiKey, string url, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (apiKey is not null)
            request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        return (client ?? _client).SendAsync(request);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? token, object? body = null, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return (client ?? _client).SendAsync(request);
    }

    private Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string url, string token, string json)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private sealed class ConfiguredFactory(Dictionary<string, string?> settings) : TeamBuilderWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
        }
    }
}
