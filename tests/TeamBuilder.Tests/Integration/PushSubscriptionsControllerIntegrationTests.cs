using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Tests.Support;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// HTTP contract of browser push registration (in-memory database): linked players only,
/// idempotent registration keyed by endpoint, credentials never returned, rotation and the
/// per-player device limit, the endpoint allowlist, 409 when the server has no VAPID identity,
/// per-caller rate limiting; plus the notification "opened" receipt and the per-player
/// "notify me" limit. Delivery itself runs against SQL Server elsewhere.
/// </summary>
public sealed class PushSubscriptionsControllerIntegrationTests : IDisposable
{
    private const string Url = "/api/v1/players/me/push-subscriptions";

    private readonly TeamBuilderWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly RefillHarness _h;
    private readonly List<SimulatedBrowser> _browsers = [];

    public PushSubscriptionsControllerIntegrationTests()
    {
        _factory = Configure(WebPushTestKeys.EnabledConfiguration(maxDevices: 3));
        _client = _factory.CreateClient();
        _h = new RefillHarness(_factory, _client, CreateContext);
    }

    public void Dispose()
    {
        foreach (var browser in _browsers) browser.Dispose();
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public void PushHttpClient_HasNoRequestLogging_BecauseEndpointUrlsAreCredentials()
    {
        // IHttpClientFactory's default handlers log every request URL at Information.
        var handler = _factory.Services.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(nameof(Infrastructure.WebPush.IWebPushClient));
        var chain = new List<string>();
        for (HttpMessageHandler? current = handler; current is not null; current = (current as DelegatingHandler)?.InnerHandler)
            chain.Add(current.GetType().Name);

        chain.Should().NotContain(name => name.Contains("Logging"), string.Join(" > ", chain));
        chain.Last().Should().Be(nameof(SocketsHttpHandler));
    }

    [Fact]
    public async Task Config_IsPublic_AndCarriesOnlyThePublicKey()
    {
        var config = await _client.GetFromJsonAsync<WebPushConfigDto>("/api/v1/push/config");
        config!.Enabled.Should().BeTrue();
        config.VapidPublicKey.Should().Be(WebPushTestKeys.Vapid.PublicKey);

        var raw = await _client.GetStringAsync("/api/v1/push/config");
        raw.Should().NotContain(WebPushTestKeys.Vapid.PrivateKey);
    }

    [Fact]
    public async Task Disabled_ReportsSo_AndRefusesRegistration_WhileInAppKeepsWorking()
    {
        using var factory = new TeamBuilderWebApplicationFactory();
        using var client = factory.CreateClient();
        var h = new RefillHarness(factory, client, () => factory.Services.CreateScope().ServiceProvider.GetRequiredService<TeamBuilderDbContext>());
        var (_, token) = await h.NewPlayerAsync();

        (await client.GetFromJsonAsync<WebPushConfigDto>("/api/v1/push/config"))!.Should().BeEquivalentTo(new WebPushConfigDto { Enabled = false, VapidPublicKey = null });
        var response = await h.SendAsync(HttpMethod.Put, Url, token, Browser().RegistrationBody());
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RefillHarness.CodeAsync(response)).Should().Be("WebPushDisabled");
        (await h.NotificationsAsync(token)).Items.Should().BeEmpty("the in-app API is unaffected");
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task Routes_RequireALinkedPlayer(string method)
    {
        var unlinked = TeamBuilderWebApplicationFactory.CreateTestJwt(LinkedPlayerTokens.NewSubject());
        var (url, body) = method switch
        {
            "PUT" => (Url, Browser().RegistrationBody()),
            "POST" => ($"{Url}/unregister", (object?)new { endpoint = "https://fcm.googleapis.com/fcm/send/x" }),
            "DELETE" => ($"{Url}/{Guid.NewGuid()}", null),
            _ => (Url, null)
        };

        (await _h.SendAsync(new HttpMethod(method), url, null, body)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _h.SendAsync(new HttpMethod(method), url, unlinked, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Register_IsIdempotentPerEndpoint_AndNeverReturnsCredentials()
    {
        var (playerId, token) = await _h.NewPlayerAsync();
        var browser = Browser();

        var created = await _h.SendAsync(HttpMethod.Put, Url, token, browser.RegistrationBody());
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var again = await _h.SendAsync(HttpMethod.Put, Url, token, browser.RegistrationBody());
        again.StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var text in new[] { await created.Content.ReadAsStringAsync(), await again.Content.ReadAsStringAsync(), await (await _h.SendAsync(HttpMethod.Get, Url, token)).Content.ReadAsStringAsync() })
        {
            text.Should().NotContain(browser.Endpoint).And.NotContain(browser.P256dh).And.NotContain(browser.Auth).And.NotContain("endpoint", "no endpoint field at all");
        }

        await using var context = CreateContext();
        var row = await context.PushSubscriptions.AsNoTracking().SingleAsync(s => s.PlayerId == playerId);
        (row.Endpoint, row.P256dh, row.Auth, row.IsActive).Should().Be((browser.Endpoint, browser.P256dh, browser.Auth, true));
        row.UserAgentFamily.Should().Be("Other");
    }

    [Fact]
    public async Task Register_RefreshesRotatedKeys_AndRetiresARotatedEndpoint()
    {
        var (playerId, token) = await _h.NewPlayerAsync();
        var first = Browser();
        await _h.SendAsync(HttpMethod.Put, Url, token, first.RegistrationBody());

        // Same endpoint, new keys (re-subscribed): the row is updated in place.
        var rekeyed = new { endpoint = first.Endpoint, keys = new { p256dh = Browser().P256dh, auth = Browser().Auth } };
        (await _h.SendAsync(HttpMethod.Put, Url, token, rekeyed)).StatusCode.Should().Be(HttpStatusCode.OK);

        // New endpoint naming the old one: the old one is deactivated as Replaced.
        var rotated = Browser();
        (await _h.SendAsync(HttpMethod.Put, Url, token, rotated.RegistrationBody(previousEndpoint: first.Endpoint))).StatusCode.Should().Be(HttpStatusCode.Created);

        await using var context = CreateContext();
        var rows = await context.PushSubscriptions.AsNoTracking().Where(s => s.PlayerId == playerId).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Single(r => r.Endpoint == first.Endpoint).Should().Match<PushSubscription>(r =>
            !r.IsActive && r.DisabledReason == PushSubscriptionDisabledReasons.Replaced && r.P256dh == rekeyed.keys.p256dh);
        rows.Single(r => r.Endpoint == rotated.Endpoint).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Register_SameBrowserByAnotherPlayer_MovesItToTheCaller()
    {
        var (aliceId, alice) = await _h.NewPlayerAsync();
        var (bobId, bob) = await _h.NewPlayerAsync();
        var shared = Browser();
        await _h.SendAsync(HttpMethod.Put, Url, alice, shared.RegistrationBody());

        (await _h.SendAsync(HttpMethod.Put, Url, bob, shared.RegistrationBody())).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var context = CreateContext();
        (await context.PushSubscriptions.AsNoTracking().SingleAsync(s => s.Endpoint == shared.Endpoint)).PlayerId.Should().Be(bobId);
        (await _h.SendAsync(HttpMethod.Get, Url, alice)).Content.ReadFromJsonAsync<List<PushSubscriptionDto>>().Result.Should().BeEmpty();
        aliceId.Should().NotBe(bobId);
    }

    [Fact]
    public async Task Register_BeyondTheDeviceLimit_RetiresTheLeastRecentlySeenBrowser()
    {
        var (_, token) = await _h.NewPlayerAsync();
        var browsers = Enumerable.Range(0, 4).Select(_ => Browser()).ToList();
        foreach (var browser in browsers)
        {
            (await _h.SendAsync(HttpMethod.Put, Url, token, browser.RegistrationBody())).StatusCode.Should().Be(HttpStatusCode.Created);
            await Task.Delay(5);
        }

        var devices = await (await _h.SendAsync(HttpMethod.Get, Url, token)).Content.ReadFromJsonAsync<List<PushSubscriptionDto>>();
        devices!.Count(d => d.IsActive).Should().Be(3, "MaxDevicesPerPlayer is 3 in this host");
        devices.Should().ContainSingle(d => !d.IsActive && d.DisabledReason == PushSubscriptionDisabledReasons.Evicted);
        await using var context = CreateContext();
        (await context.PushSubscriptions.AsNoTracking().SingleAsync(s => s.Endpoint == browsers[0].Endpoint)).IsActive.Should().BeFalse("the oldest goes first");
    }

    [Theory]
    [InlineData("https://attacker.example/collect", null, null)]
    [InlineData("http://fcm.googleapis.com/fcm/send/x", null, null)]
    [InlineData("https://169.254.169.254/latest/meta-data", null, null)]
    [InlineData(null, "AAAA", null)]
    [InlineData(null, null, "AAAA")]
    public async Task Register_RejectsUnsafeEndpointsAndMalformedKeys_WithoutEchoingThem(string? endpoint, string? p256dh, string? auth)
    {
        var (_, token) = await _h.NewPlayerAsync();
        var browser = Browser();
        var body = new { endpoint = endpoint ?? browser.Endpoint, keys = new { p256dh = p256dh ?? browser.P256dh, auth = auth ?? browser.Auth } };

        var response = await _h.SendAsync(HttpMethod.Put, Url, token, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain(body.endpoint).And.NotContain(body.keys.p256dh).And.NotContain(body.keys.auth);
    }

    [Fact]
    public async Task Unregister_DeletesOnlyTheCallersCopy_AndIsIdempotent()
    {
        var (_, alice) = await _h.NewPlayerAsync();
        var (_, bob) = await _h.NewPlayerAsync();
        var browser = Browser();
        await _h.SendAsync(HttpMethod.Put, Url, alice, browser.RegistrationBody());

        (await _h.SendAsync(HttpMethod.Post, $"{Url}/unregister", bob, new { endpoint = browser.Endpoint })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using (var context = CreateContext())
            (await context.PushSubscriptions.CountAsync(s => s.Endpoint == browser.Endpoint)).Should().Be(1, "bob cannot remove alice's browser");

        (await _h.SendAsync(HttpMethod.Post, $"{Url}/unregister", alice, new { endpoint = browser.Endpoint })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _h.SendAsync(HttpMethod.Post, $"{Url}/unregister", alice, new { endpoint = browser.Endpoint })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using (var context = CreateContext())
            (await context.PushSubscriptions.CountAsync(s => s.Endpoint == browser.Endpoint)).Should().Be(0, "credentials are deleted, not kept");
    }

    [Fact]
    public async Task Delete_ById_IsOwnerOnly()
    {
        var (_, alice) = await _h.NewPlayerAsync();
        var (_, bob) = await _h.NewPlayerAsync();
        var created = await (await _h.SendAsync(HttpMethod.Put, Url, alice, Browser().RegistrationBody())).Content.ReadFromJsonAsync<PushSubscriptionDto>();

        (await _h.SendAsync(HttpMethod.Delete, $"{Url}/{created!.Id}", bob)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _h.SendAsync(HttpMethod.Delete, $"{Url}/{created.Id}", alice)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _h.SendAsync(HttpMethod.Delete, $"{Url}/{created.Id}", alice)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Mutations_AreRateLimitedPerCaller()
    {
        var config = WebPushTestKeys.EnabledConfiguration();
        config["RateLimiting:PushSubscriptions:PermitLimit"] = "2";
        using var factory = Configure(config);
        using var client = factory.CreateClient();
        var h = new RefillHarness(factory, client, () => factory.Services.CreateScope().ServiceProvider.GetRequiredService<TeamBuilderDbContext>());
        var (_, token) = await h.NewPlayerAsync();
        var (_, other) = await h.NewPlayerAsync();
        var browser = Browser();

        (await h.SendAsync(HttpMethod.Put, Url, token, browser.RegistrationBody())).StatusCode.Should().Be(HttpStatusCode.Created);
        (await h.SendAsync(HttpMethod.Put, Url, token, browser.RegistrationBody())).StatusCode.Should().Be(HttpStatusCode.OK);
        var limited = await h.SendAsync(HttpMethod.Put, Url, token, browser.RegistrationBody());
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.Should().NotBeNull();
        (await h.SendAsync(HttpMethod.Put, Url, other, Browser().RegistrationBody())).StatusCode.Should().Be(HttpStatusCode.Created, "partitioned by caller, not shared");
        (await h.SendAsync(HttpMethod.Get, Url, token)).StatusCode.Should().Be(HttpStatusCode.OK, "reads are not limited");
    }

    [Fact]
    public async Task Opened_MarksReadOnce_AndIsOwnerOnly()
    {
        var (aliceId, alice) = await _h.NewPlayerAsync();
        var (_, bob) = await _h.NewPlayerAsync();
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 2);
        var notificationId = Guid.NewGuid();
        await using (var context = CreateContext())
        {
            context.InAppNotifications.Add(new InAppNotification
            {
                Id = notificationId, PlayerId = aliceId, Type = "roster.vacancy", OccurrenceId = occurrenceId,
                RosterRequirementId = requirementId, SourceEventId = Guid.NewGuid(), Title = "Basketball spot opened", Body = "A participant spot opened."
            });
            await context.SaveChangesAsync();
        }
        var url = $"/api/v1/players/me/notifications/{notificationId}/opened";

        (await _h.SendAsync(HttpMethod.Post, url, bob, new { via = "push" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _h.SendAsync(HttpMethod.Post, url, alice, new { via = "push", clickToOpenMs = 420 })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _h.SendAsync(HttpMethod.Post, url, alice)).StatusCode.Should().Be(HttpStatusCode.NoContent, "idempotent, body optional");

        await using var check = CreateContext();
        var row = await check.InAppNotifications.AsNoTracking().SingleAsync(n => n.Id == notificationId);
        row.OpenedAtUtc.Should().NotBeNull();
        row.ReadAtUtc.Should().NotBeNull();
        (await _h.UnreadCountAsync(alice)).Should().Be(0);
    }

    [Fact]
    public async Task NotifyMe_IsCappedPerPlayerOnOpenGames_NotPerGame()
    {
        var config = WebPushTestKeys.EnabledConfiguration();
        config["RefillLimits:MaxActiveOccurrenceSubscriptionsPerPlayer"] = "2";
        using var factory = Configure(config);
        using var client = factory.CreateClient();
        var h = new RefillHarness(factory, client, () => factory.Services.CreateScope().ServiceProvider.GetRequiredService<TeamBuilderDbContext>());
        var (_, token) = await h.NewPlayerAsync();
        var games = new List<(Guid OccurrenceId, Guid RequirementId)>();
        for (var i = 0; i < 3; i++)
        {
            var (_, _, occurrenceId, requirementId) = await h.SeedGameAsync(requiredCount: 1);
            games.Add((occurrenceId, requirementId));
        }

        (await h.SubscribeAsync(token, games[0].OccurrenceId, games[0].RequirementId)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await h.SubscribeAsync(token, games[1].OccurrenceId, games[1].RequirementId)).StatusCode.Should().Be(HttpStatusCode.Created);
        var third = await h.SubscribeAsync(token, games[2].OccurrenceId, games[2].RequirementId);
        third.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RefillHarness.CodeAsync(third)).Should().Be(RosterConflictCodes.SubscriptionLimitReached);
        (await h.SubscribeAsync(token, games[0].OccurrenceId, games[0].RequirementId)).StatusCode.Should().Be(HttpStatusCode.OK, "an existing one is still idempotent");

        await h.SetEventStatusAsync(games[0].OccurrenceId, TeamBuilder.Domain.Enums.EventStatus.Completed);
        (await h.SubscribeAsync(token, games[2].OccurrenceId, games[2].RequirementId)).StatusCode.Should().Be(HttpStatusCode.Created, "closed games do not count");

        // Many players on one game are never capped.
        var (_, _, popular, popularRequirement) = await h.SeedGameAsync(requiredCount: 1);
        for (var i = 0; i < 5; i++)
        {
            var (_, fan) = await h.NewPlayerAsync();
            (await h.SubscribeAsync(fan, popular, popularRequirement)).StatusCode.Should().Be(HttpStatusCode.Created);
        }
    }

    private SimulatedBrowser Browser()
    {
        var browser = new SimulatedBrowser($"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}");
        _browsers.Add(browser);
        return browser;
    }

    private TeamBuilderDbContext CreateContext() => _factory.Services.CreateScope().ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

    private static TeamBuilderWebApplicationFactory Configure(Dictionary<string, string?> settings) => new ConfiguredFactory(settings);

    private sealed class ConfiguredFactory(Dictionary<string, string?> settings) : TeamBuilderWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
        }
    }
}
