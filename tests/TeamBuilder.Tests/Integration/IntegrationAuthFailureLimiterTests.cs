using System.Net;
using System.Threading.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TeamBuilder.Api.Auth;
using TeamBuilder.Api.RateLimiting;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;

namespace TeamBuilder.Tests.Integration;

public sealed class IntegrationAuthFailureLimiterTests
{
    private static readonly Guid KeyId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid PlayerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void FixedWindowLease_DisposalDoesNotRefundAnAcquiredFailurePermit()
    {
        using var limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 1,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = false
        });

        using (var lease = limiter.AttemptAcquire())
            lease.IsAcquired.Should().BeTrue();

        limiter.GetStatistics()!.CurrentAvailablePermits.Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentReservations_NeverExceedThePerClientBudget()
    {
        const int budget = 4;
        const int requestCount = 40;
        using var limiter = CreateLimiter(budget);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, requestCount).Select(async index =>
        {
            _ = index;
            await start.Task;
            var admitted = limiter.TryReserve("client-a", out var reservation, out var retryAfter);
            _ = retryAfter;
            return (admitted, reservation);
        }).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(attempts);
        results.Count(r => r.admitted).Should().Be(budget);
        results.Count(r => !r.admitted).Should().Be(requestCount - budget);
        results.Where(r => r.admitted).All(r => r.reservation is not null).Should().BeTrue();
        foreach (var result in results.Where(r => r.admitted))
            result.reservation!.CommitFailure();

        limiter.TryReserve("client-a", out _, out var retryAfter).Should().BeFalse();
        retryAfter.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void SuccessfulRequests_ReleaseReservationsWithoutSpendingFailureBudget()
    {
        using var limiter = CreateLimiter(3);

        for (var i = 0; i < 100; i++)
        {
            using var valid = Reserve(limiter, "shared-client");
        }

        for (var i = 0; i < 3; i++)
            Reserve(limiter, "shared-client").CommitFailure();

        limiter.TryReserve("shared-client", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void SharedClient_ValidAndInvalidLookupsUseReservationsAndOnlyInvalidResultsCommit()
    {
        using var limiter = CreateLimiter(2);
        var invalid = Reserve(limiter, "shared-client");
        var valid = Reserve(limiter, "shared-client");

        limiter.TryReserve("shared-client", out _, out var temporaryRetry).Should().BeFalse();
        temporaryRetry.Should().BeGreaterThan(TimeSpan.Zero);

        invalid.CommitFailure();
        valid.Dispose();
        using (Reserve(limiter, "shared-client")) { }

        Reserve(limiter, "shared-client").CommitFailure();
        limiter.TryReserve("shared-client", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void CancelledOrThrowingLookup_ReleasesReservationWhenDisposed()
    {
        using var limiter = CreateLimiter(1);

        try
        {
            using var cancelled = Reserve(limiter, "client");
            throw new OperationCanceledException();
        }
        catch (OperationCanceledException)
        {
        }

        Action throwingLookup = () =>
        {
            using var reservation = Reserve(limiter, "client");
            throw new InvalidOperationException("Synthetic service failure.");
        };
        throwingLookup.Should().Throw<InvalidOperationException>();
        using var afterException = Reserve(limiter, "client");
    }

    [Fact]
    public void DifferentClients_HaveIndependentBudgets()
    {
        using var limiter = CreateLimiter(1);
        Reserve(limiter, "client-a").CommitFailure();

        using var clientB = Reserve(limiter, "client-b");
        limiter.TryReserve("client-a", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void WindowExpiry_ResetsIdleBudgetAndRemovesIdlePartition()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        using var limiter = CreateLimiter(1, windowSeconds: 10, maxTrackedClients: 1, clock);
        Reserve(limiter, "old-client").CommitFailure();
        limiter.TrackedClientCount.Should().Be(1);
        limiter.TryReserve("new-client", out _, out _).Should().BeFalse("the configured partition cap is fail-closed");

        clock.Advance(TimeSpan.FromSeconds(11));
        limiter.CleanupExpiredClients();
        limiter.TrackedClientCount.Should().Be(0);

        using var nextClient = Reserve(limiter, "new-client");
    }

    [Fact]
    public void ActiveReservationAcrossWindowBoundary_BlocksUntilResolvedThenStartsFreshWindow()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        using var limiter = CreateLimiter(1, windowSeconds: 10, timeProvider: clock);
        var outstanding = Reserve(limiter, "client");

        clock.Advance(TimeSpan.FromSeconds(11));
        limiter.TryReserve("client", out _, out var retryAfter).Should().BeFalse();
        retryAfter.Should().BeGreaterThan(TimeSpan.Zero);

        outstanding.Dispose();
        using var nextWindow = Reserve(limiter, "client");
    }

    [Fact]
    public async Task RepeatedBursts_DoNotLeakReservationsOrShrinkCapacity()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        using var limiter = CreateLimiter(5, windowSeconds: 10, timeProvider: clock);

        for (var burst = 0; burst < 20; burst++)
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = Enumerable.Range(0, 40).Select(async index =>
            {
                _ = index;
                await start.Task;
                var admitted = limiter.TryReserve("client", out var reservation, out var retryAfter);
                _ = retryAfter;
                return (admitted, reservation);
            }).ToArray();
            start.SetResult();
            var leases = await Task.WhenAll(attempts);
            leases.Count(r => r.admitted).Should().Be(5);
            foreach (var lease in leases.Where(r => r.admitted))
                lease.reservation!.Dispose();
            clock.Advance(TimeSpan.FromSeconds(11));
            limiter.CleanupExpiredClients();
        }

        var finalBurst = Enumerable.Range(0, 5).Select(_ => Reserve(limiter, "client")).ToArray();
        foreach (var lease in finalBurst)
            lease.Dispose();
    }

    [Fact]
    public async Task InvalidConcurrentRequests_AreStoppedBeforeTheAuthenticationService()
    {
        const int budget = 3;
        const int requestCount = 24;
        var calls = 0;
        var allLookupsAdmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLookups = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var throttledResponses = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var throttledCount = 0;
        var keys = new FakeIntegrationApiKeyService(async (key, cancellationToken) =>
        {
            if (Interlocked.Increment(ref calls) == budget)
                allLookupsAdmitted.TrySetResult();
            await releaseLookups.Task.WaitAsync(cancellationToken);
            return null;
        });

        using var factory = new AuthTestFactory(keys, budget);
        using var client = factory.CreateClient();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, requestCount).Select(async _ =>
        {
            await start.Task;
            using var request = NewRequest("invalid");
            var response = await client.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && Interlocked.Increment(ref throttledCount) == requestCount - budget)
                throttledResponses.TrySetResult();
            return response;
        }).ToArray();

        start.SetResult();
        await allLookupsAdmitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await throttledResponses.Task.WaitAsync(TimeSpan.FromSeconds(10));
        calls.Should().Be(budget, "the lookup service is deliberately held until every excess request is denied");

        releaseLookups.SetResult();
        var responses = await Task.WhenAll(requests);
        responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized).Should().Be(budget);
        responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests).Should().Be(requestCount - budget);
        foreach (var response in responses)
            response.Dispose();
    }

    [Fact]
    public async Task UntrustedForwardedFor_CannotChooseANewFailurePartition()
    {
        var keys = new FakeIntegrationApiKeyService((_, _) => Task.FromResult<IntegrationApiKeyIdentity?>(null));
        using var factory = new AuthTestFactory(keys, permitLimit: 2);
        using var client = factory.CreateClient();

        using var first = await SendWithForwardedForAsync(client, "198.51.100.10");
        using var second = await SendWithForwardedForAsync(client, "203.0.113.20");
        first.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        second.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var throttled = await SendWithForwardedForAsync(client, "192.0.2.30");
        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter.Should().NotBeNull();
    }

    [Fact]
    public async Task ServiceExceptionAndRequestCancellation_ReleaseReservations()
    {
        var cancellationLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var keys = new FakeIntegrationApiKeyService((key, cancellationToken) =>
        {
            Interlocked.Increment(ref calls);
            if (key == "throws")
                throw new InvalidOperationException("Synthetic authentication service failure.");
            if (key == "cancels")
            {
                cancellationLookup.TrySetResult();
                return WaitForCancellationAsync(cancellationToken);
            }
            return Task.FromResult<IntegrationApiKeyIdentity?>(new IntegrationApiKeyIdentity(KeyId, PlayerId, [IntegrationScopes.RostersRead]));
        });

        using var factory = new AuthTestFactory(keys, permitLimit: 1);
        using var client = factory.CreateClient();

        var failedRequest = SendAsync(client, "throws");
        try
        {
            using var failedResponse = await failedRequest;
            failedResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            // TestServer can surface an unhandled service exception directly; either way, the next request proves release.
        }
        (await SendAsync(client, "valid-after-exception")).StatusCode.Should().Be(HttpStatusCode.OK);

        using var cancellation = new CancellationTokenSource();
        var cancelledRequest = SendAsync(client, "cancels", cancellation.Token);
        await cancellationLookup.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelledRequest);

        (await SendAsync(client, "valid-after-cancellation")).StatusCode.Should().Be(HttpStatusCode.OK);
        calls.Should().Be(4);
    }

    private static IntegrationAuthFailureLimiter CreateLimiter(
        int permitLimit,
        int windowSeconds = 60,
        int maxTrackedClients = 10000,
        TimeProvider? timeProvider = null) =>
        new(Options.Create(new IntegrationAuthFailureRateLimitOptions
        {
            PermitLimit = permitLimit,
            WindowSeconds = windowSeconds,
            MaxTrackedClients = maxTrackedClients
        }), timeProvider ?? TimeProvider.System);

    private static IntegrationAuthFailureLimiter.Reservation Reserve(IntegrationAuthFailureLimiter limiter, string client)
    {
        limiter.TryReserve(client, out var reservation, out _).Should().BeTrue();
        return reservation!;
    }

    private static HttpRequestMessage NewRequest(string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/integration/v1/me");
        request.Headers.TryAddWithoutValidation("X-Api-Key", key);
        return request;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string key, CancellationToken cancellationToken = default)
    {
        using var request = NewRequest(key);
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<IntegrationApiKeyIdentity?> WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return null;
    }

    private static async Task<HttpResponseMessage> SendWithForwardedForAsync(HttpClient client, string forwardedFor)
    {
        using var request = NewRequest("invalid");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        return await client.SendAsync(request);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private long _utcTicks = now.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan amount) => Interlocked.Add(ref _utcTicks, amount.Ticks);
    }

    private sealed class AuthTestFactory(FakeIntegrationApiKeyService keys, int permitLimit) : TeamBuilderWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:IntegrationAuthFailures:PermitLimit"] = permitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["RateLimiting:IntegrationAuthFailures:WindowSeconds"] = "60"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IIntegrationApiKeyService>();
                services.AddSingleton<IIntegrationApiKeyService>(keys);
            });
        }
    }

    private sealed class FakeIntegrationApiKeyService(
        Func<string, CancellationToken, Task<IntegrationApiKeyIdentity?>> authenticate) : IIntegrationApiKeyService
    {
        public Task<CreatedIntegrationApiKeyDto> CreateAsync(Guid playerId, CreateIntegrationApiKeyDto request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<IntegrationApiKeyDto>> ListAsync(Guid playerId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RevokeAsync(Guid playerId, Guid keyId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IntegrationApiKeyIdentity?> AuthenticateAsync(string presentedKey, CancellationToken cancellationToken = default) =>
            authenticate(presentedKey, cancellationToken);

        public Task<IntegrationCallerDto?> DescribeAsync(Guid keyId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IntegrationCallerDto?>(new IntegrationCallerDto
            {
                KeyId = KeyId,
                PlayerId = PlayerId,
                KeyName = "test",
                DisplayName = "Test Player",
                Scopes = [IntegrationScopes.RostersRead]
            });
    }
}
