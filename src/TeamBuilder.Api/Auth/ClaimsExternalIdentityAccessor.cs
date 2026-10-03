using Microsoft.Extensions.Options;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;

namespace TeamBuilder.Api.Auth;

/// <summary>
/// Claims-aware implementation of <see cref="IExternalIdentityAccessor"/>, configured by
/// <see cref="ExternalIdentityOptions"/> (<c>Jwt:ExternalIdentity</c>).
/// </summary>
internal sealed class ClaimsExternalIdentityAccessor(
    IHttpContextAccessor httpContextAccessor,
    IOptions<ExternalIdentityOptions> options) : IExternalIdentityAccessor
{
    public ExternalIdentity? Current
    {
        get
        {
            ExternalIdentityAuthentication.TryResolve(
                httpContextAccessor.HttpContext?.User, options.Value, out var identity, out _);
            return identity;
        }
    }
}
