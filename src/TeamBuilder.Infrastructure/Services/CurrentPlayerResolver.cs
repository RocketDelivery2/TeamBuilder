using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Infrastructure.Services;

/// <summary>
/// EF Core implementation of <see cref="ICurrentPlayerResolver"/>. Claims extraction stays in
/// <see cref="IExternalIdentityAccessor"/>; this class only maps the resulting identity to a
/// player through <c>PlayerIdentities</c>.
/// </summary>
public class CurrentPlayerResolver(
    IExternalIdentityAccessor externalIdentityAccessor,
    TeamBuilderDbContext context) : ICurrentPlayerResolver
{
    private readonly IExternalIdentityAccessor _externalIdentityAccessor = externalIdentityAccessor;
    private readonly TeamBuilderDbContext _context = context;

    public async Task<Guid?> ResolvePlayerIdAsync(CancellationToken cancellationToken = default)
    {
        var identity = _externalIdentityAccessor.Current;
        if (identity == null)
        {
            return null;
        }

        // Issuer + Subject are exact keys: never lowercased, trimmed, or URI-normalized. The
        // binary collation makes the SQL comparison case-sensitive, but SQL Server '=' still
        // ignores trailing spaces, so candidates are re-checked ordinally. The unique index
        // allows at most one candidate.
        var candidates = await _context.PlayerIdentities
            .AsNoTracking()
            .Where(pi => pi.Issuer == identity.Issuer && pi.Subject == identity.Subject)
            .Select(pi => new { pi.Issuer, pi.Subject, pi.PlayerId })
            .ToListAsync(cancellationToken);

        var match = candidates.FirstOrDefault(c =>
            string.Equals(c.Issuer, identity.Issuer, StringComparison.Ordinal) &&
            string.Equals(c.Subject, identity.Subject, StringComparison.Ordinal));

        return match?.PlayerId;
    }
}
