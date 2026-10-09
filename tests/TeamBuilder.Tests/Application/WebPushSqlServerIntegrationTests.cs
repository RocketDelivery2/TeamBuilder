using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.WebPush;
using TeamBuilder.Tests.Integration;
using TeamBuilder.Tests.Support;
using Xunit.Abstractions;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Web Push delivery on a real SQL Server 2022 database, through the real outbox handler,
/// dispatcher, RFC 8291 encryption and VAPID signing, against a deterministic fake push
/// gateway (never a live push service). Covers the delivery ledger committed with the
/// notifications, per-device failure handling (expired, rejected, transient, exhausted),
/// idempotency under replay and concurrent dispatchers, privacy of what the browser receives,
/// and the full basketball dogfood from vacancy to READY again.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class WebPushSqlServerIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly List<SimulatedBrowser> _browsers = [];
    private SqlServerTestDatabase _db = null!;
    private SqlServerWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private RefillHarness _h = null!;
    private readonly FakePushGateway _gateway = new();

    public WebPushSqlServerIntegrationTests(SqlServerContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "webpush");
        await _db.MigrateToAsync();
        _factory = new SqlServerWebApplicationFactory(_db.ConnectionString, WebPushTestKeys.EnabledConfiguration());
        _client = _factory.CreateClient();
        _h = new RefillHarness(_factory, _client, _db.CreateContext);
    }

    public async Task DisposeAsync()
    {
        foreach (var browser in _browsers) browser.Dispose();
        _client.Dispose();
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Vacancy_QueuesOnePushPerActiveDevice_WithTheNotifications_AndEachBrowserReadsASparsePayload()
    {
        var (occurrenceId, requirementId, holders) = await FullGameAsync(2);
        var (samId, sam) = await SubscriberAsync(occurrenceId, requirementId);
        var phone = await RegisterAsync(sam);
        var laptop = await RegisterAsync(sam);
        var (patId, pat) = await SubscriberAsync(occurrenceId, requirementId); // in-app only, no browser

        await LeaveAsync(holders[0], occurrenceId);
        (await _h.Processor().ProcessBatchAsync()).Processed.Should().Be(1);

        await using (var context = _db.CreateContext())
        {
            var notifications = await context.InAppNotifications.AsNoTracking().Where(n => n.OccurrenceId == occurrenceId).ToListAsync();
            notifications.Select(n => n.PlayerId).Should().BeEquivalentTo([samId, patId]);
            var samNotification = notifications.Single(n => n.PlayerId == samId).Id;
            var deliveries = await context.PushDeliveries.AsNoTracking().ToListAsync();
            deliveries.Should().HaveCount(2).And.OnlyContain(d => d.InAppNotificationId == samNotification && d.Status == PushDeliveryStatus.Pending);
        }

        var batch = await Dispatcher().ProcessBatchAsync();

        (batch.Claimed, batch.Accepted, batch.Failed).Should().Be((2, 2, 0));
        var leaver = await UsernameAsync(holders[0].PlayerId);
        foreach (var browser in new[] { phone, laptop })
        {
            var request = _gateway.To(browser.Endpoint).Should().ContainSingle().Subject;
            var json = Encoding.UTF8.GetString(browser.Decrypt(request.Body));
            var payload = JsonDocument.Parse(json).RootElement;
            payload.GetProperty("title").GetString().Should().Be("Basketball spot opened");
            payload.GetProperty("body").GetString().Should().Be("A participant spot opened in Wednesday Basketball.");
            payload.GetProperty("url").GetString().Should().StartWith($"/games/{occurrenceId}?n=");
            payload.GetProperty("occurrenceId").GetGuid().Should().Be(occurrenceId);
            json.Should().NotContain(leaver).And.NotContain(holders[0].PlayerId.ToString()).And.NotContain("@");
            request.Headers["Topic"].Should().Be(occurrenceId.ToString("N"));
            int.Parse(request.Headers["TTL"]).Should().BeInRange(1, 1800);
        }
        _gateway.Requests.Should().HaveCount(2, "pat has no browser registered and still has the in-app notification");
        (await DeliveriesAsync()).Should().OnlyContain(d => d.Status == PushDeliveryStatus.Accepted && d.LastStatusCode == 201 && d.CompletedAtUtc != null);
        pat.Should().NotBeNull();
    }

    [Fact]
    public async Task ExpiredEndpoint_IsDisabled_WhileTheHealthyDeviceAndOtherPlayersStillGetIt()
    {
        var (occurrenceId, requirementId, holders) = await FullGameAsync(3);
        var (_, sam) = await SubscriberAsync(occurrenceId, requirementId);
        var deadPhone = await RegisterAsync(sam);
        var laptop = await RegisterAsync(sam);
        var (_, pat) = await SubscriberAsync(occurrenceId, requirementId);
        var patPhone = await RegisterAsync(pat);
        _gateway.Behavior = r => r.Endpoint == deadPhone.Endpoint ? HttpStatusCode.Gone : HttpStatusCode.Created;

        await LeaveAsync(holders[0], occurrenceId);
        await _h.Processor().ProcessBatchAsync();
        var batch = await Dispatcher().ProcessBatchAsync();

        (batch.Accepted, batch.Failed, batch.Retried, batch.SubscriptionsDisabled).Should().Be((2, 1, 0, 1));
        await using (var context = _db.CreateContext())
        {
            var dead = await context.PushSubscriptions.AsNoTracking().SingleAsync(s => s.Endpoint == deadPhone.Endpoint);
            (dead.IsActive, dead.DisabledReason).Should().Be((false, PushSubscriptionDisabledReasons.Expired));
            (await context.PushSubscriptions.AsNoTracking().SingleAsync(s => s.Endpoint == laptop.Endpoint)).IsActive.Should().BeTrue();
            (await context.PushDeliveries.AsNoTracking().SingleAsync(d => d.Status == PushDeliveryStatus.Failed)).Should()
                .Match<PushDelivery>(d => d.LastStatusCode == 410 && d.LastError == "Gone");
        }
        _gateway.To(patPhone.Endpoint).Should().ContainSingle();

        // The next vacancy no longer queues anything for the dead phone.
        _gateway.Requests.Clear();
        await LeaveAsync(holders[1], occurrenceId);
        await _h.Processor().ProcessBatchAsync();
        (await Dispatcher().ProcessBatchAsync()).Claimed.Should().Be(2);
        _gateway.To(deadPhone.Endpoint).Should().BeEmpty();
    }

    [Fact]
    public async Task TransientFailures_AreRetriedWithBackoff_HonouringRetryAfter_ThenDelivered()
    {
        var (occurrenceId, requirementId, holders) = await FullGameAsync(2);
        var (_, sam) = await SubscriberAsync(occurrenceId, requirementId);
        var phone = await RegisterAsync(sam);
        var calls = 0;
        _gateway.Behavior = _ => ++calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Created;
        await LeaveAsync(holders[0], occurrenceId);
        await _h.Processor().ProcessBatchAsync();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow.AddSeconds(2));
        var dispatcher = Dispatcher(clock, o => o.RetryBaseDelay = TimeSpan.FromSeconds(30));

        (await dispatcher.ProcessBatchAsync()).Retried.Should().Be(1);
        var pending = (await DeliveriesAsync()).Single();
        (pending.Status, pending.AttemptCount, pending.LastStatusCode, pending.LastError).Should().Be((PushDeliveryStatus.Pending, 1, 503, "ServerError"));
        pending.NextAttemptAtUtc.Should().BeCloseTo(clock.UtcNow.UtcDateTime.AddSeconds(30), TimeSpan.FromSeconds(1));

        (await dispatcher.ProcessBatchAsync()).Claimed.Should().Be(0, "not due yet");
        clock.UtcNow = clock.UtcNow.AddSeconds(31);
        (await dispatcher.ProcessBatchAsync()).Accepted.Should().Be(1);
        _gateway.To(phone.Endpoint).Should().HaveCount(2);
        (await DeliveriesAsync()).Single().Should().Match<PushDelivery>(d => d.Status == PushDeliveryStatus.Accepted && d.AttemptCount == 2);
    }

    [Fact]
    public async Task TransientFailures_GiveUpAfterMaxAttempts_AndRepeatedFailuresDisableTheDevice()
    {
        var (occurrenceId, requirementId, holders) = await FullGameAsync(3);
        var (_, sam) = await SubscriberAsync(occurrenceId, requirementId);
        var flaky = await RegisterAsync(sam);
        _gateway.Behavior = _ => HttpStatusCode.InternalServerError;
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow.AddSeconds(2));
        var dispatcher = Dispatcher(clock, o =>
        {
            o.MaxAttempts = 2;
            o.RetryBaseDelay = TimeSpan.Zero;
            o.MaxConsecutiveFailures = 2;
        });

        for (var vacancy = 0; vacancy < 2; vacancy++)
        {
            await LeaveAsync(holders[vacancy], occurrenceId);
            await _h.Processor().ProcessBatchAsync();
            (await dispatcher.ProcessBatchAsync()).Retried.Should().Be(1);
            (await dispatcher.ProcessBatchAsync()).Failed.Should().Be(1);
        }

        (await DeliveriesAsync()).Should().HaveCount(2).And.OnlyContain(d =>
            d.Status == PushDeliveryStatus.Failed && d.AttemptCount == 2 && d.LastError == "RetriesExhausted:ServerError");
        await using var context = _db.CreateContext();
        var subscription = await context.PushSubscriptions.AsNoTracking().SingleAsync(s => s.Endpoint == flaky.Endpoint);
        (subscription.IsActive, subscription.FailureCount, subscription.DisabledReason).Should().Be((false, 2, PushSubscriptionDisabledReasons.Rejected));
        (await context.OutboxMessages.AsNoTracking().CountAsync(m => m.Status == OutboxMessageStatus.Completed)).Should().Be(2, "the outbox never waited on the browser");
    }

    [Fact]
    public async Task ARejectedRequest_FailsThatDeliveryOnly_AndASuccessResetsTheFailureCount()
    {
        var (occurrenceId, requirementId, holders) = await FullGameAsync(3);
        var (_, sam) = await SubscriberAsync(occurrenceId, requirementId);
        var phone = await RegisterAsync(sam);
        _gateway.Behavior = _ => HttpStatusCode.BadRequest;
        await LeaveAsync(holders[0], occurrenceId);
        await _h.Processor().ProcessBatchAsync();
        (await Dispatcher().ProcessBatchAsync()).Failed.Should().Be(1);
        (await SubscriptionAsync(phone)).Should().Match<PushSubscription>(s => s.IsActive && s.FailureCount == 1);

        _gateway.Behavior = _ => HttpStatusCode.Created;
        await LeaveAsync(holders[1], occurrenceId);
        await _h.Processor().ProcessBatchAsync();
        (await Dispatcher().ProcessBatchAsync()).Accepted.Should().Be(1);
        (await SubscriptionAsync(phone)).FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task ReplayedOutboxMessages_AndConcurrentDispatchers_NeverDuplicateNotificationsOrPushes()
    {
        var (occurrenceId, requirementId, holders) = await FullGameAsync(2);
        var browsers = new List<SimulatedBrowser>();
        for (var i = 0; i < 6; i++)
        {
            var (_, token) = await SubscriberAsync(occurrenceId, requirementId);
            browsers.Add(await RegisterAsync(token));
            browsers.Add(await RegisterAsync(token));
        }
        await LeaveAsync(holders[0], occurrenceId);
        var processor = _h.Processor();
        await processor.ProcessBatchAsync();

        // Worker replay (a lost completion) and a second worker on the same message.
        var messageId = (await _h.OutboxAsync(occurrenceId)).Single().Id;
        for (var replay = 0; replay < 2; replay++)
        {
            await using (var context = _db.CreateContext())
                await context.OutboxMessages.Where(m => m.Id == messageId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, OutboxMessageStatus.Pending));
            await Task.WhenAll(processor.ProcessBatchAsync(), _h.Processor().ProcessBatchAsync());
        }
        (await _h.NotificationRowsAsync(occurrenceId)).Should().HaveCount(6);
        (await DeliveriesAsync()).Should().HaveCount(12, "deliveries are created only with new notifications");

        _gateway.Latency = TimeSpan.FromMilliseconds(20);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Dispatcher(configure: o => o.BatchSize = 3).ProcessBatchAsync()));
        var drained = 0;
        PushBatchResult next;
        while ((next = await Dispatcher().ProcessBatchAsync()).Claimed > 0)
            drained += next.Claimed;

        // READPAST lets a dispatcher skip rows another is claiming, so a batch can come back
        // short; what must hold is that no delivery is claimed twice and none is lost.
        results.Sum(r => r.Claimed).Should().BeInRange(1, 12);
        (results.Sum(r => r.Claimed) + drained).Should().Be(12, "the concurrent batches were disjoint");
        _gateway.Requests.Should().HaveCount(12);
        _gateway.Requests.GroupBy(r => r.Endpoint).Should().OnlyContain(g => g.Count() == 1);
        (await DeliveriesAsync()).Should().OnlyContain(d => d.Status == PushDeliveryStatus.Accepted && d.AttemptCount == 1);
    }

    [Fact]
    public async Task ACrashedDispatchersLease_Expires_AndAnotherDispatcherSendsIt()
    {
        var (occurrenceId, requirementId, holders) = await FullGameAsync(2);
        var (_, sam) = await SubscriberAsync(occurrenceId, requirementId);
        var phone = await RegisterAsync(sam);
        await LeaveAsync(holders[0], occurrenceId);
        await _h.Processor().ProcessBatchAsync();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow.AddSeconds(2));

        var (_, claimed) = await Dispatcher(clock).ClaimAsync(CancellationToken.None); // then "crashes"
        claimed.Should().ContainSingle();
        (await Dispatcher(clock).ProcessBatchAsync()).Claimed.Should().Be(0, "leased");

        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        (await Dispatcher(clock).ProcessBatchAsync()).Accepted.Should().Be(1);
        _gateway.To(phone.Endpoint).Should().ContainSingle();
        (await DeliveriesAsync()).Single().AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task ABrowserNowSignedInAsSomeoneElse_OrUnregistered_IsNeverSentTheOldAlert()
    {
        var (occurrenceId, requirementId, holders) = await FullGameAsync(2);
        var (_, sam) = await SubscriberAsync(occurrenceId, requirementId);
        var shared = await RegisterAsync(sam);
        var removed = await RegisterAsync(sam);
        await LeaveAsync(holders[0], occurrenceId);
        await _h.Processor().ProcessBatchAsync();

        var (_, alex) = await _h.NewPlayerAsync();
        (await _h.SendAsync(HttpMethod.Put, "/api/v1/players/me/push-subscriptions", alex, shared.RegistrationBody())).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _h.SendAsync(HttpMethod.Post, "/api/v1/players/me/push-subscriptions/unregister", sam, new { endpoint = removed.Endpoint })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var batch = await Dispatcher().ProcessBatchAsync();

        (batch.Abandoned, batch.Accepted).Should().Be((2, 0));
        _gateway.Requests.Should().BeEmpty();
        (await DeliveriesAsync()).Select(d => d.LastError).Should().BeEquivalentTo(["SubscriptionReassigned", "SubscriptionGone"]);
    }

    [Fact]
    public async Task AnAlertOlderThanItsTtl_IsAbandoned_NotSent()
    {
        var (occurrenceId, requirementId, holders) = await FullGameAsync(2);
        var (_, sam) = await SubscriberAsync(occurrenceId, requirementId);
        await RegisterAsync(sam);
        await LeaveAsync(holders[0], occurrenceId);
        await _h.Processor().ProcessBatchAsync();

        var late = new FixedTimeProvider(DateTimeOffset.UtcNow.AddMinutes(31));
        (await Dispatcher(late).ProcessBatchAsync()).Abandoned.Should().Be(1);
        _gateway.Requests.Should().BeEmpty();
        (await DeliveriesAsync()).Single().LastError.Should().Be("Expired");
    }

    [Fact]
    public async Task WithWebPushDisabled_NoDeliveriesAreQueued_AndInAppStillWorks()
    {
        await using var factory = new SqlServerWebApplicationFactory(_db.ConnectionString);
        using var client = factory.CreateClient();
        var h = new RefillHarness(factory, client, _db.CreateContext);
        var (_, _, occurrenceId, requirementId) = await h.SeedGameAsync(requiredCount: 1);
        var holder = (await h.FillAsync(occurrenceId, requirementId, 1))[0];
        var (_, sam) = await h.NewPlayerAsync();
        await h.SubscribeAsync(sam, occurrenceId, requirementId);
        await using (var context = _db.CreateContext())
        {
            // A browser registered while push was on, then the operator turned it off.
            var playerId = await context.PlayerIdentities.Select(p => p.PlayerId).FirstAsync();
            context.PushSubscriptions.Add(new PushSubscription
            {
                Id = Guid.NewGuid(), PlayerId = playerId, Endpoint = "https://fcm.googleapis.com/fcm/send/off",
                EndpointHash = Infrastructure.Services.PushSubscriptionService.HashEndpoint("https://fcm.googleapis.com/fcm/send/off"),
                P256dh = "x", Auth = "y", LastSeenAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        await h.LeaveAsync(holder.Token, occurrenceId, holder.AssignmentId);
        (await h.Processor().ProcessBatchAsync()).Processed.Should().Be(1);

        (await h.UnreadCountAsync(sam)).Should().Be(1);
        (await DeliveriesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Dogfood_FullGame_PushToABackgroundedBrowser_ClickOpensTheGame_ClaimReturnsToReady()
    {
        using var metrics = new RefillMetricsRecorder();

        // 1. Host creates and fills a 10-player basketball game.
        var (occurrenceId, requirementId, players) = await FullGameAsync(10);
        (await _h.DetailAsync(occurrenceId)).IsRosterReady.Should().BeTrue();

        // 2-3. An extra player subscribes and enables browser alerts on two browsers.
        var (extraId, extra) = await SubscriberAsync(occurrenceId, requirementId);
        var phone = await RegisterAsync(extra);
        var laptop = await RegisterAsync(extra);
        _gateway.Behavior = r => r.Endpoint == laptop.Endpoint ? HttpStatusCode.Gone : HttpStatusCode.Created;

        // 4-6. (The tab is in the background.) A participant leaves; the request commits fast.
        var started = System.Diagnostics.Stopwatch.StartNew();
        (await _h.LeaveAsync(players[4].Token, occurrenceId, players[4].AssignmentId)).StatusCode.Should().Be(HttpStatusCode.OK);
        _output.WriteLine($"leave committed in {started.ElapsedMilliseconds} ms");

        // 7. The vacancy is in the outbox, transactionally with the leave.
        (await _h.OutboxAsync(occurrenceId)).Should().ContainSingle(m => m.Status == OutboxMessageStatus.Pending);

        // 8. The worker creates the in-app notification (and queues the pushes in the same commit).
        await _h.Processor().ProcessBatchAsync();
        var notification = (await _h.NotificationRowsAsync(occurrenceId)).Should().ContainSingle().Subject;
        notification.PlayerId.Should().Be(extraId);

        // 9-10. The push goes out; the phone's browser receives it, the dead laptop is retired.
        var batch = await Dispatcher().ProcessBatchAsync();
        (batch.Accepted, batch.Failed).Should().Be((1, 1));
        var received = PushPayload.FromUtf8Json(phone.Decrypt(_gateway.To(phone.Endpoint).Single().Body));
        (received.Title, received.OccurrenceId, received.NotificationId).Should().Be(("Basketball spot opened", occurrenceId, notification.Id));

        // 11-12. The click opens the exact game (the service worker adds via=push and the click time).
        var gameUrl = new Uri(new Uri("https://teambuilder.test"), received.Url);
        gameUrl.AbsolutePath.Should().Be($"/games/{occurrenceId}");
        (await _h.SendAsync(HttpMethod.Post, $"/api/v1/players/me/notifications/{notification.Id}/opened", extra, new { via = "push", clickToOpenMs = 350 }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // 13. The current roster is re-read: 9/10.
        var detail = await _h.DetailAsync(occurrenceId, extra);
        (detail.SupplyCount, detail.RequiredCount, detail.IsRosterReady).Should().Be((9, 10, false));

        // 14-15. The player claims through the normal atomic claim; READY 10/10.
        (await _h.ClaimAsync(extra, occurrenceId, requirementId)).StatusCode.Should().Be(HttpStatusCode.Created);
        var ready = await _h.DetailAsync(occurrenceId, extra);
        (ready.SupplyCount, ready.IsRosterReady).Should().Be((10, true));
        (await _h.UnreadCountAsync(extra)).Should().Be(0, "opening it marked it read");

        // Funnel metrics were recorded end to end.
        foreach (var instrument in new[]
                 {
                     "teambuilder.refill.vacancy_to_notification", "teambuilder.refill.vacancy_to_push_accepted",
                     "teambuilder.refill.push_click_to_game_open", "teambuilder.refill.game_open_to_claim_attempt",
                     "teambuilder.refill.vacancy_to_replacement"
                 })
        {
            metrics.Values(instrument).Should().NotBeEmpty(instrument);
            _output.WriteLine($"{instrument}: {string.Join(", ", metrics.Values(instrument).Select(v => $"{v:F0} ms"))}");
        }
        metrics.Sum("teambuilder.push.attempted").Should().BeGreaterThanOrEqualTo(2);
        metrics.Sum("teambuilder.push.accepted").Should().BeGreaterThanOrEqualTo(1);
        metrics.Sum("teambuilder.push.permanent_failure").Should().BeGreaterThanOrEqualTo(1);
        metrics.Sum("teambuilder.push.subscription_disabled").Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task StaleAlerts_SpotFilledOrGameCancelledBeforeTheClick_AreResolvedByTheFreshRead()
    {
        var (occurrenceId, requirementId, holders) = await FullGameAsync(2);
        var (_, sam) = await SubscriberAsync(occurrenceId, requirementId);
        var phone = await RegisterAsync(sam);
        await LeaveAsync(holders[0], occurrenceId);
        await _h.Processor().ProcessBatchAsync();
        await Dispatcher().ProcessBatchAsync();
        _gateway.To(phone.Endpoint).Should().ContainSingle();

        // Someone else claims first: the clicked alert finds the game full, and the claim says so.
        var (_, quick) = await _h.NewPlayerAsync();
        (await _h.ClaimAsync(quick, occurrenceId, requirementId)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await _h.DetailAsync(occurrenceId, sam)).OpenQuantity.Should().Be(0);
        var late = await _h.ClaimAsync(sam, occurrenceId, requirementId);
        late.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RefillHarness.CodeAsync(late)).Should().Be(RosterConflictCodes.RequirementFull);

        // A second opening, then the host cancels before the click: closed, nothing to claim.
        await LeaveAsync(holders[1], occurrenceId);
        await _h.Processor().ProcessBatchAsync();
        await Dispatcher().ProcessBatchAsync();
        await _h.SetEventStatusAsync(occurrenceId, EventStatus.Cancelled);
        (await _h.DetailAsync(occurrenceId, sam)).AcceptsRosterChanges.Should().BeFalse();
        var closed = await _h.ClaimAsync(sam, occurrenceId, requirementId);
        (await RefillHarness.CodeAsync(closed)).Should().Be(RosterConflictCodes.OccurrenceClosed);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private PushDispatcher Dispatcher(TimeProvider? clock = null, Action<WebPushOptions>? configure = null)
    {
        var options = new WebPushOptions
        {
            Enabled = true,
            Subject = "mailto:qa@teambuilder.test",
            VapidPublicKey = WebPushTestKeys.Vapid.PublicKey,
            VapidPrivateKey = WebPushTestKeys.Vapid.PrivateKey,
            BatchSize = 100
        };
        configure?.Invoke(options);
        var (client, _) = WebPushProtocolTests.Client(_gateway);
        return new PushDispatcher(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            client,
            clock ?? new FixedTimeProvider(DateTimeOffset.UtcNow.AddSeconds(2)),
            Options.Create(options),
            NullLogger<PushDispatcher>.Instance);
    }

    private async Task<(Guid OccurrenceId, Guid RequirementId, List<(Guid PlayerId, string Token, Guid AssignmentId)> Holders)> FullGameAsync(int size)
    {
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: size);
        return (occurrenceId, requirementId, await _h.FillAsync(occurrenceId, requirementId, size));
    }

    private async Task<(Guid PlayerId, string Token)> SubscriberAsync(Guid occurrenceId, Guid requirementId)
    {
        var (playerId, token) = await _h.NewPlayerAsync();
        (await _h.SubscribeAsync(token, occurrenceId, requirementId)).StatusCode.Should().Be(HttpStatusCode.Created);
        return (playerId, token);
    }

    private async Task<SimulatedBrowser> RegisterAsync(string token)
    {
        var browser = new SimulatedBrowser($"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}");
        _browsers.Add(browser);
        var response = await _h.SendAsync(HttpMethod.Put, "/api/v1/players/me/push-subscriptions", token, browser.RegistrationBody());
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return browser;
    }

    private async Task LeaveAsync((Guid PlayerId, string Token, Guid AssignmentId) holder, Guid occurrenceId) =>
        (await _h.LeaveAsync(holder.Token, occurrenceId, holder.AssignmentId)).StatusCode.Should().Be(HttpStatusCode.OK);

    private async Task<List<PushDelivery>> DeliveriesAsync()
    {
        await using var context = _db.CreateContext();
        return await context.PushDeliveries.AsNoTracking().ToListAsync();
    }

    private async Task<PushSubscription> SubscriptionAsync(SimulatedBrowser browser)
    {
        await using var context = _db.CreateContext();
        return await context.PushSubscriptions.AsNoTracking().SingleAsync(s => s.Endpoint == browser.Endpoint);
    }

    private async Task<string> UsernameAsync(Guid playerId)
    {
        await using var context = _db.CreateContext();
        return await context.Players.Where(p => p.Id == playerId).Select(p => p.Username).SingleAsync();
    }
}
