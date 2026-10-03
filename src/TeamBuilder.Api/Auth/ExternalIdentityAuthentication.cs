using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using TeamBuilder.Application.Models;

namespace TeamBuilder.Api.Auth;

/// <summary>
/// The JWT bearer scheme used by endpoints that work with the caller's external identity
/// (<c>/api/v1/players/me</c>). It requires a usable Issuer + Subject; the subject is an opaque,
/// issuer-scoped identifier and is not a TeamBuilder player ID.
/// </summary>
public static class ExternalIdentityAuthentication
{
    public const string SchemeName = "ExternalIdentity";

    // Column limits from PlayerIdentityConfiguration; longer values could never be stored.
    internal const int MaxIssuerLength = 512;
    internal const int MaxSubjectLength = 255;
    internal const int MaxProviderLength = 100;
    internal const int MaxTenantIdLength = 100;

    /// <summary>
    /// Reads Issuer (the validated token's <c>iss</c> claim), Subject (the configured subject claim),
    /// and the provider/tenant metadata from <paramref name="principal"/>.
    /// </summary>
    public static bool TryResolve(
        ClaimsPrincipal? principal,
        ExternalIdentityOptions options,
        out ExternalIdentity? identity,
        out string? error)
    {
        identity = null;
        error = null;

        if (principal?.Identity?.IsAuthenticated != true)
        {
            error = "The request is not authenticated.";
            return false;
        }

        var issuer = principal.FindFirstValue(JwtRegisteredClaimNames.Iss);
        if (string.IsNullOrWhiteSpace(issuer) || issuer.Length > MaxIssuerLength)
        {
            error = $"Missing or invalid '{JwtRegisteredClaimNames.Iss}' claim.";
            return false;
        }

        var subject = principal.FindFirstValue(options.SubjectClaim);
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > MaxSubjectLength)
        {
            error = $"Missing or invalid '{options.SubjectClaim}' claim.";
            return false;
        }

        var tenantId = string.IsNullOrWhiteSpace(options.TenantIdClaim)
            ? null
            : principal.FindFirstValue(options.TenantIdClaim);
        if (tenantId is not null && (string.IsNullOrWhiteSpace(tenantId) || tenantId.Length > MaxTenantIdLength))
        {
            error = $"Invalid '{options.TenantIdClaim}' claim.";
            return false;
        }

        var provider = options.Provider;
        if (string.IsNullOrWhiteSpace(provider) || provider.Length > MaxProviderLength)
        {
            error = "The configured Jwt:ExternalIdentity:Provider is missing or too long.";
            return false;
        }

        identity = new ExternalIdentity(issuer, subject, provider, tenantId);
        return true;
    }
}
