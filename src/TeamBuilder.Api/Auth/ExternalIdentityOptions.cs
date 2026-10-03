namespace TeamBuilder.Api.Auth;

/// <summary>
/// Settings for resolving the caller's external identity, bound from <c>Jwt:ExternalIdentity</c>.
/// Separate from the legacy <c>Jwt:PlayerIdClaim</c>, which names a claim that must carry a
/// TeamBuilder player GUID; the external subject here is an opaque, issuer-scoped string.
/// </summary>
public sealed class ExternalIdentityOptions
{
    public const string SectionName = "Jwt:ExternalIdentity";

    /// <summary>
    /// Claim carrying the stable, issuer-scoped user identifier. Use <c>oid</c> for Microsoft Entra,
    /// <c>sub</c> for most other OIDC providers.
    /// </summary>
    public string SubjectClaim { get; set; } = "sub";

    /// <summary>Optional claim carrying a tenant identifier (Entra: <c>tid</c>). Metadata only.</summary>
    public string TenantIdClaim { get; set; } = "tid";

    /// <summary>Descriptive provider name stored with new identity links (e.g. <c>entra</c>). Metadata only.</summary>
    public string Provider { get; set; } = "oidc";
}
