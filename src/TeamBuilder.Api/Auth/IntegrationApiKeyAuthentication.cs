using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TeamBuilder.Api.RateLimiting;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;

namespace TeamBuilder.Api.Auth;

/// <summary>
/// The authentication scheme of the public integration API (<c>/api/integration/v1</c>): an
/// integration API key in the <c>X-Api-Key</c> header. It is never a default scheme, so a key
/// authenticates nothing under <c>/api/v1</c>, and a user's bearer token authenticates nothing
/// here. The key is never logged or echoed; failures say only that the key was not accepted.
/// </summary>
public static class IntegrationApiKeyAuthentication
{
    public const string SchemeName = "IntegrationApiKey";
    public const string HeaderName = "X-Api-Key";

    public const string KeyIdClaim = "tb_api_key_id";
    public const string PlayerIdClaim = "tb_player_id";
    public const string ScopeClaim = "scope";

    /// <summary>Policy for the v1 read endpoints: an active key holding <see cref="IntegrationScopes.RostersRead"/>.</summary>
    public const string RostersReadPolicy = "Integration:" + IntegrationScopes.RostersRead;

    public static AuthenticationBuilder AddIntegrationApiKey(this AuthenticationBuilder builder)
    {
        builder.Services.AddSingleton<IntegrationAuthFailureLimiter>();
        builder.Services.TryAddSingleton(TimeProvider.System);
        return builder.AddScheme<AuthenticationSchemeOptions, IntegrationApiKeyAuthenticationHandler>(SchemeName, _ => { });
    }

    public static AuthorizationBuilder AddIntegrationPolicies(this AuthorizationBuilder builder) =>
        builder.AddPolicy(RostersReadPolicy, policy => policy
            .AddAuthenticationSchemes(SchemeName)
            .RequireAuthenticatedUser()
            .RequireClaim(ScopeClaim, IntegrationScopes.RostersRead));

    /// <summary>The calling key's id and owner, from a principal this scheme produced.</summary>
    public static bool TryGetCaller(ClaimsPrincipal user, out Guid keyId, out Guid playerId)
    {
        playerId = Guid.Empty;
        return Guid.TryParse(user.FindFirstValue(KeyIdClaim), out keyId)
            & Guid.TryParse(user.FindFirstValue(PlayerIdClaim), out playerId);
    }
}

internal sealed class IntegrationApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IIntegrationApiKeyService keys,
    IntegrationAuthFailureLimiter failures)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private TimeSpan? _throttledFor;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(IntegrationApiKeyAuthentication.HeaderName, out var values) || values.Count == 0)
            return AuthenticateResult.NoResult();

        var client = Context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!failures.TryReserve(client, out var reservation, out var retryAfter))
        {
            _throttledFor = retryAfter;
            return AuthenticateResult.Fail("Too many rejected API keys from this client.");
        }

        using (var authenticationReservation = reservation!)
        {
            if (values.Count > 1)
            {
                authenticationReservation.CommitFailure();
                return AuthenticateResult.Fail("More than one API key was sent.");
            }

            var identity = await keys.AuthenticateAsync(values[0]?.Trim() ?? string.Empty, Context.RequestAborted);
            if (identity is null)
            {
                authenticationReservation.CommitFailure();
                return AuthenticateResult.Fail("The API key was not accepted.");
            }

            var claims = new List<Claim>
            {
                new(IntegrationApiKeyAuthentication.KeyIdClaim, identity.KeyId.ToString()),
                new(IntegrationApiKeyAuthentication.PlayerIdClaim, identity.PlayerId.ToString())
            };
            claims.AddRange(identity.Scopes.Select(s => new Claim(IntegrationApiKeyAuthentication.ScopeClaim, s)));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
        }
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (_throttledFor is { } retryAfter)
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too Many Requests",
                Detail = "Too many rejected API keys from this client. Wait for Retry-After and try again."
            }, options: null, contentType: "application/problem+json", Context.RequestAborted);
            return;
        }

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"ApiKey header=\"{IntegrationApiKeyAuthentication.HeaderName}\"";
        await Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Unauthorized",
            Detail = $"Send a valid, active API key in the {IntegrationApiKeyAuthentication.HeaderName} header."
        }, options: null, contentType: "application/problem+json", Context.RequestAborted);
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Forbidden",
            Detail = "This API key does not have the scope this endpoint needs."
        }, options: null, contentType: "application/problem+json", Context.RequestAborted);
    }
}

/// <summary>
/// Reserves per-client admission before an integration-key lookup. A reservation becomes a
/// committed failure only when lookup returns no identity; success, cancellation and exceptions
/// release it. State is process-local and bounded by MaxTrackedClients.
/// </summary>
internal sealed class IntegrationAuthFailureLimiter : IDisposable
{
    private const int CleanupIntervalSeconds = 60;
    private static readonly TimeSpan CapacitySweepInterval = TimeSpan.FromSeconds(1);
    private readonly ConcurrentDictionary<string, ClientState> _clients = new(StringComparer.Ordinal);
    private readonly object _partitionGate = new();
    private readonly Timer _cleanupTimer;
    private readonly TimeProvider _timeProvider;
    private readonly int _permitLimit;
    private readonly int _maxTrackedClients;
    private readonly TimeSpan _window;
    private DateTimeOffset _lastCapacitySweepAt = DateTimeOffset.MinValue;
    private bool _disposed;

    public IntegrationAuthFailureLimiter(IOptions<IntegrationAuthFailureRateLimitOptions> options, TimeProvider timeProvider)
    {
        var limits = options.Value;
        _permitLimit = limits.PermitLimit;
        _maxTrackedClients = limits.MaxTrackedClients;
        _window = TimeSpan.FromSeconds(limits.WindowSeconds);
        _timeProvider = timeProvider;
        _cleanupTimer = new Timer(_ => CleanupExpiredClients(), null,
            TimeSpan.FromSeconds(CleanupIntervalSeconds), TimeSpan.FromSeconds(CleanupIntervalSeconds));
    }

    /// <summary>Try to reserve one of the client's remaining fixed-window authentication slots.</summary>
    public bool TryReserve(string client, out Reservation? reservation, out TimeSpan retryAfter)
    {
        reservation = null;
        while (true)
        {
            var state = GetOrCreateState(client);
            if (state is null)
            {
                retryAfter = TimeSpan.FromSeconds(1);
                return false;
            }

            var now = _timeProvider.GetUtcNow();
            var result = state.TryReserve(now, _window, _permitLimit, out retryAfter);
            if (result == ReservationResult.Retired)
                continue;
            if (result == ReservationResult.Denied)
                return false;

            reservation = new Reservation(state);
            return true;
        }
    }

    internal int TrackedClientCount => _clients.Count;

    internal void CleanupExpiredClients()
    {
        lock (_partitionGate)
        {
            if (_disposed)
                return;

            var now = _timeProvider.GetUtcNow();
            foreach (var pair in _clients)
            {
                if (pair.Value.TryRetire(now, _window))
                    _clients.TryRemove(new KeyValuePair<string, ClientState>(pair.Key, pair.Value));
            }
        }
    }

    private ClientState? GetOrCreateState(string client)
    {
        if (_clients.TryGetValue(client, out var existing))
            return existing;

        lock (_partitionGate)
        {
            if (_disposed)
                return null;
            if (_clients.TryGetValue(client, out existing))
                return existing;

            var now = _timeProvider.GetUtcNow();
            if (_clients.Count >= _maxTrackedClients && now - _lastCapacitySweepAt >= CapacitySweepInterval)
            {
                CleanupExpiredClientsUnderLock(now);
                _lastCapacitySweepAt = now;
            }
            if (_clients.Count >= _maxTrackedClients)
                return null;

            var state = new ClientState(now);
            return _clients.TryAdd(client, state) ? state : _clients.GetValueOrDefault(client);
        }
    }

    private void CleanupExpiredClientsUnderLock(DateTimeOffset now)
    {
        foreach (var pair in _clients)
        {
            if (pair.Value.TryRetire(now, _window))
                _clients.TryRemove(new KeyValuePair<string, ClientState>(pair.Key, pair.Value));
        }
    }

    public void Dispose()
    {
        lock (_partitionGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _cleanupTimer.Dispose();
            _clients.Clear();
        }
    }

    internal sealed class Reservation : IDisposable
    {
        private ClientState? _state;

        internal Reservation(ClientState state) => _state = state;

        public void CommitFailure() => Resolve(failed: true);

        public void Dispose() => Resolve(failed: false);

        private void Resolve(bool failed)
        {
            var state = Interlocked.Exchange(ref _state, null);
            state?.Resolve(failed);
        }
    }

    internal enum ReservationResult { Reserved, Denied, Retired }

    internal sealed class ClientState(DateTimeOffset windowStartedAt)
    {
        private readonly object _gate = new();
        private DateTimeOffset _windowStartedAt = windowStartedAt;
        private int _committedFailures;
        private int _reservations;
        private bool _retired;

        public ReservationResult TryReserve(DateTimeOffset now, TimeSpan window, int permitLimit, out TimeSpan retryAfter)
        {
            lock (_gate)
            {
                if (_retired)
                {
                    retryAfter = TimeSpan.Zero;
                    return ReservationResult.Retired;
                }

                var windowEnd = _windowStartedAt + window;
                if (now >= windowEnd && _reservations == 0)
                {
                    _windowStartedAt = now;
                    _committedFailures = 0;
                    windowEnd = now + window;
                }

                if (_committedFailures + _reservations >= permitLimit || now >= windowEnd)
                {
                    retryAfter = _committedFailures >= permitLimit
                        ? windowEnd - now
                        : TimeSpan.FromSeconds(1);
                    if (retryAfter < TimeSpan.FromSeconds(1))
                        retryAfter = TimeSpan.FromSeconds(1);
                    return ReservationResult.Denied;
                }

                _reservations++;
                retryAfter = TimeSpan.Zero;
                return ReservationResult.Reserved;
            }
        }

        public void Resolve(bool failed)
        {
            lock (_gate)
            {
                if (_reservations <= 0)
                    throw new InvalidOperationException("Integration authentication reservation accounting underflowed.");
                _reservations--;
                if (failed)
                    _committedFailures++;
            }
        }

        public bool TryRetire(DateTimeOffset now, TimeSpan window)
        {
            lock (_gate)
            {
                if (_retired || _reservations != 0 || now < _windowStartedAt + window)
                    return false;
                _retired = true;
                return true;
            }
        }
    }
}
