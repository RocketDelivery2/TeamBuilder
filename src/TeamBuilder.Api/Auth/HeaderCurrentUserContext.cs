using TeamBuilder.Application.Interfaces;

namespace TeamBuilder.Api.Auth;

/// <summary>
/// Temporary implementation that reads caller identity from the
/// <c>X-User-Id</c> HTTP request header. This will be replaced by a
/// claims-based implementation once real authentication is added.
/// </summary>
internal sealed class HeaderCurrentUserContext : ICurrentUserContext
{
    private const string HeaderName = "X-User-Id";

    private readonly Guid _userId;

    public HeaderCurrentUserContext(IHttpContextAccessor httpContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);

        var headerValue = httpContextAccessor.HttpContext?.Request.Headers[HeaderName].FirstOrDefault();

        _userId = Guid.TryParse(headerValue, out var parsed) ? parsed : Guid.Empty;
    }

    /// <inheritdoc/>
    public Guid UserId => _userId;
}
