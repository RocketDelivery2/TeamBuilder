using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using TeamBuilder.Application.Interfaces;

namespace TeamBuilder.Api.Auth;

/// <summary>
/// Claims-aware implementation of <see cref="ICurrentUserContext"/>.
/// Reads the configured <c>Jwt:PlayerIdClaim</c> (default <c>sub</c>) from the
/// authenticated <see cref="ClaimsPrincipal"/>. Returns <see cref="Guid.Empty"/>
/// when no authenticated principal is present or the claim is missing/invalid.
/// </summary>
internal sealed class ClaimsCurrentUserContext : ICurrentUserContext
{
    /// <summary>Default claim type used to carry the player ID in a JWT.</summary>
    internal const string DefaultPlayerIdClaim = "sub";

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

        _userId = Guid.Empty;
    }

    /// <inheritdoc/>
    public Guid UserId => _userId;
}
