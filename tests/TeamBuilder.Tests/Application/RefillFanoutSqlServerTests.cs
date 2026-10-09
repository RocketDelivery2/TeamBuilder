using System.Diagnostics;
using System.Net;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.WebPush;
using TeamBuilder.Tests.Integration;
using TeamBuilder.Tests.Support;
using Xunit.Abstractions;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Vacancy fanout at 1, 10, 100 and 1000 subscribers (each with one browser) on a real SQL
/// Server 2022 database, push gateway mocked with zero latency. Measures what the director
/// asked for: leave commit latency, outbox processing latency (notification and push-ledger
/// inserts in one commit), and push dispatch preparation (claim, encrypt, sign, record). Every
/// subscriber is notified: the list is never truncated. The numbers are printed for the
/// record; the assertions are deliberately generous ceilings so a slow CI runner cannot flake,
/// while a pathological regression (per-row round trips, an unbounded transaction) still fails.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class RefillFanoutSqlServerTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _fixture;
    private readonly ITestOutputHelper _output;
    private SqlServerTestDatabase _db = null!;
    private SqlServerWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private RefillHarness _h = null!;
    private readonly FakePushGateway _gateway = new();

    public RefillFanoutSqlServerTests(SqlServerContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "fanout");
        await _db.MigrateToAsync();
        _factory = new SqlServerWebApplicationFactory(_db.ConnectionString, WebPushTestKeys.EnabledConfiguration());
        _client = _factory.CreateClient();
        _h = new RefillHarness(_factory, _client, _db.CreateContext);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task Fanout_NotifiesAndPushesEverySubscriber(int subscribers)
    {
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 2);
        var holders = await _h.FillAsync(occurrenceId, requirementId, 2);
        using var browser = new SimulatedBrowser("https://fcm.googleapis.com/fcm/send/fanout");
        await SeedSubscribersAsync(occurrenceId, requirementId, subscribers, browser);

        // Warm the request path once (JIT, connection pool) so the leave measures the commit.
        await _h.DetailAsync(occurrenceId);

        var leave = Stopwatch.StartNew();
        (await _h.LeaveAsync(holders[0].Token, occurrenceId, holders[0].AssignmentId)).StatusCode.Should().Be(HttpStatusCode.OK);
        leave.Stop();

        var outbox = Stopwatch.StartNew();
        (await _h.Processor().ProcessBatchAsync()).Processed.Should().Be(1);
        outbox.Stop();

        await using (var context = _db.CreateContext())
        {
            (await context.InAppNotifications.CountAsync(n => n.OccurrenceId == occurrenceId)).Should().Be(subscribers, "the subscriber list is never truncated");
            (await context.PushDeliveries.CountAsync(d => d.Status == PushDeliveryStatus.Pending)).Should().Be(subscribers);
        }

        var dispatcher = Dispatcher();
        var dispatch = Stopwatch.StartNew();
        var accepted = 0;
        var batches = 0;
        PushBatchResult batch;
        while ((batch = await dispatcher.ProcessBatchAsync()).Claimed > 0)
        {
            accepted += batch.Accepted;
            batches++;
        }
        dispatch.Stop();

        accepted.Should().Be(subscribers);
        _gateway.Requests.Should().HaveCount(subscribers);
        _output.WriteLine(
            $"FANOUT n={subscribers}: leave commit {leave.ElapsedMilliseconds} ms; outbox processing (notifications + push ledger) {outbox.ElapsedMilliseconds} ms; " +
            $"push dispatch preparation {dispatch.ElapsedMilliseconds} ms in {batches} batch(es) ({(double)dispatch.ElapsedMilliseconds / subscribers:F2} ms/device, gateway mocked)");

        leave.ElapsedMilliseconds.Should().BeLessThan(5_000, "the leave never waits on fanout");
        outbox.ElapsedMilliseconds.Should().BeLessThan(60_000);
        dispatch.ElapsedMilliseconds.Should().BeLessThan(120_000);
    }

    private PushDispatcher Dispatcher()
    {
        var options = new WebPushOptions
        {
            Enabled = true,
            Subject = "mailto:qa@teambuilder.test",
            VapidPublicKey = WebPushTestKeys.Vapid.PublicKey,
            VapidPrivateKey = WebPushTestKeys.Vapid.PrivateKey
        };
        var (client, _) = WebPushProtocolTests.Client(_gateway);
        return new PushDispatcher(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            client,
            new FixedTimeProvider(DateTimeOffset.UtcNow.AddSeconds(2)),
            Options.Create(options),
            NullLogger<PushDispatcher>.Instance);
    }

    /// <summary>Set-based seed: N players, each subscribed to the requirement with one active browser.</summary>
    private async Task SeedSubscribersAsync(Guid occurrenceId, Guid requirementId, int count, SimulatedBrowser browser)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            DECLARE @players TABLE ([Id] uniqueidentifier NOT NULL);
            INSERT INTO [Players] ([Id], [Username], [CreatedAtUtc])
            OUTPUT inserted.[Id] INTO @players
            SELECT NEWID(), CONCAT('fan-', CONVERT(varchar(36), NEWID())), SYSUTCDATETIME()
            FROM (SELECT TOP (@count) 1 AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) x;
            INSERT INTO [OccurrenceRosterSubscriptions] ([Id], [PlayerId], [OccurrenceId], [RosterRequirementId], [CreatedAtUtc])
            SELECT NEWID(), [Id], @occurrence, @requirement, SYSUTCDATETIME() FROM @players;
            INSERT INTO [PushSubscriptions] ([Id], [PlayerId], [Endpoint], [EndpointHash], [P256dh], [Auth], [LastSeenAtUtc], [IsActive], [FailureCount], [CreatedAtUtc])
            SELECT NEWID(), p.[Id], e.[Endpoint], HASHBYTES('SHA2_256', e.[Endpoint]), @p256dh, @auth, SYSUTCDATETIME(), 1, 0, SYSUTCDATETIME()
            FROM @players p CROSS APPLY (SELECT CONCAT(@endpoint, '/', CONVERT(varchar(36), p.[Id])) AS [Endpoint]) e;
            """, connection);
        command.Parameters.AddWithValue("@count", count);
        command.Parameters.AddWithValue("@occurrence", occurrenceId);
        command.Parameters.AddWithValue("@requirement", requirementId);
        command.Parameters.AddWithValue("@p256dh", browser.P256dh);
        command.Parameters.AddWithValue("@auth", browser.Auth);
        command.Parameters.AddWithValue("@endpoint", browser.Endpoint);
        await command.ExecuteNonQueryAsync();
    }
}
