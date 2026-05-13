using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using TeamBuilder.Application.Interfaces;

namespace TeamBuilder.Api.Auth;

/// <summary>
/// Claims-aware implementation of <see cref="ICurrentUserContext"/>.
/// <para>
/// Resolution order:
/// <list type="number">
///   <item>If the request carries a valid, authenticated <see cref="ClaimsPrincipal"/>,
///         the configured <c>Jwt:PlayerIdClaim</c> (default <c>sub</c>) is used as the player ID.</item>
///   <item>Otherwise the <c>X-User-Id</c> header is read as a fallback so that the existing
///         development workflow is preserved during the transition period.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class ClaimsCurrentUserContext : ICurrentUserContext
{
    /// <summary>Default claim type used to carry the player ID in a JWT.</summary>
    internal const string DefaultPlayerIdClaim = "sub";

    private const string XUserIdHeader = "X-User-Id";

    private readonly Guid _userId;

    public ClaimsCurrentUserContext(IHttpContextAccessor httpContextAccessor, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        ArgumentNullException.ThrowIfNull(configuration);

        var httpContext = httpContextAccessor.HttpContext;

        if (httpContext is not null && httpContext.User.Identity?.IsAuthenticated == true)
        {
            var claimType = configuration["Jwt:PlayerIdClaim"] ?? DefaultPlayerIdClaim;
            var claimValue = httpContext.User.FindFirstValue(claimType);
            _userId = Guid.TryParse(claimValue, out var fromClaim) ? fromClaim : Guid.Empty;
            return;
        }

        // Transition fallback: honour X-User-Id when no JWT is present.
        var headerValue = httpContext?.Request.Headers[XUserIdHeader].FirstOrDefault();
        _userId = Guid.TryParse(headerValue, out var fromHeader) ? fromHeader : Guid.Empty;
    }

    /// <inheritdoc/>
    public Guid UserId => _userId;
}
