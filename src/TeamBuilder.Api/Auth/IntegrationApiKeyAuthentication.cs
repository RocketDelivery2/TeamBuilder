using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
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

    public static AuthenticationBuilder AddIntegrationApiKey(this AuthenticationBuilder builder) =>
        builder.AddScheme<AuthenticationSchemeOptions, IntegrationApiKeyAuthenticationHandler>(SchemeName, _ => { });

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
    IIntegrationApiKeyService keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(IntegrationApiKeyAuthentication.HeaderName, out var values) || values.Count == 0)
            return AuthenticateResult.NoResult();
        if (values.Count > 1)
            return AuthenticateResult.Fail("More than one API key was sent.");

        var identity = await keys.AuthenticateAsync(values[0]?.Trim() ?? string.Empty, Context.RequestAborted);
        if (identity is null)
            return AuthenticateResult.Fail("The API key was not accepted.");

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
