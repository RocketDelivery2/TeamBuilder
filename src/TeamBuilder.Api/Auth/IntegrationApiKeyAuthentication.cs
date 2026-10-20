using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
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
        if (failures.IsExhausted(client, out var retryAfter))
        {
            _throttledFor = retryAfter;
            return AuthenticateResult.Fail("Too many rejected API keys from this client.");
        }
        if (values.Count > 1)
        {
            failures.Record(client);
            return AuthenticateResult.Fail("More than one API key was sent.");
        }

        var identity = await keys.AuthenticateAsync(values[0]?.Trim() ?? string.Empty, Context.RequestAborted);
        if (identity is null)
        {
            failures.Record(client);
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
/// Counts rejected integration API keys per client IP (see
/// <see cref="IntegrationAuthFailureRateLimitOptions"/>). A request is checked before the key
/// lookup and only a rejection spends a permit, so key guessing is bounded per IP while
/// clients with valid keys are never throttled by it.
/// </summary>
internal sealed class IntegrationAuthFailureLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;
    private readonly TimeSpan _window;

    public IntegrationAuthFailureLimiter(IOptions<IntegrationAuthFailureRateLimitOptions> options)
    {
        var limits = options.Value;
        _window = TimeSpan.FromSeconds(limits.WindowSeconds);
        _limiter = PartitionedRateLimiter.Create<string, string>(client => RateLimitPartition.GetFixedWindowLimiter(
            client,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limits.PermitLimit,
                Window = _window,
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    }

    /// <summary>True when the client has no rejections left in this window (a zero-permit probe spends nothing).</summary>
    public bool IsExhausted(string client, out TimeSpan retryAfter)
    {
        using var probe = _limiter.AttemptAcquire(client, permitCount: 0);
        retryAfter = probe.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : _window;
        return !probe.IsAcquired;
    }

    public void Record(string client) => _limiter.AttemptAcquire(client).Dispose();

    public void Dispose() => _limiter.Dispose();
}
