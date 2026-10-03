using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// Tokens and <see cref="PlayerIdentity"/> links for the Teams and JoinRequests write endpoints,
/// which authenticate with the ExternalIdentity scheme and resolve the caller's internal
/// <c>Player.Id</c> from Issuer + Subject. Subjects are deliberately not GUIDs.
/// </summary>
internal static class LinkedPlayerTokens
{
    /// <summary>A token whose only identity claim is <c>sub</c> = <paramref name="subject"/>.</summary>
    internal static string ForSubject(string subject, string issuer = TeamBuilderWebApplicationFactory.TestIssuer)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TeamBuilderWebApplicationFactory.TestSigningKey));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = TeamBuilderWebApplicationFactory.TestAudience,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = subject,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString()
            },
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        });
    }

    /// <summary>An opaque, non-GUID external subject.</summary>
    internal static string NewSubject() => $"ext|{Guid.NewGuid():N}";

    /// <summary>
    /// Returns a token for <paramref name="playerId"/>: creates the player if it does not exist and
    /// links it to a non-GUID subject under <paramref name="issuer"/>, reusing an existing link.
    /// </summary>
    internal static async Task<string> ForPlayerAsync(
        IServiceProvider services,
        Guid playerId,
        string issuer = TeamBuilderWebApplicationFactory.TestIssuer)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

        var subject = await db.PlayerIdentities
            .Where(pi => pi.PlayerId == playerId && pi.Issuer == issuer)
            .Select(pi => pi.Subject)
            .FirstOrDefaultAsync();
        if (subject is not null)
            return ForSubject(subject, issuer);

        subject = NewSubject();
        await LinkAsync(db, playerId, subject, issuer);
        return ForSubject(subject, issuer);
    }

    /// <summary>Links <paramref name="playerId"/> (created if missing) to Issuer + Subject.</summary>
    internal static async Task LinkAsync(IServiceProvider services, Guid playerId, string subject, string issuer)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        await LinkAsync(db, playerId, subject, issuer);
    }

    private static async Task LinkAsync(TeamBuilderDbContext db, Guid playerId, string subject, string issuer)
    {
        if (!await db.Players.AnyAsync(p => p.Id == playerId))
        {
            db.Players.Add(new Player
            {
                Id = playerId,
                Username = $"linked-{Guid.NewGuid():N}",
                CreatedAtUtc = DateTime.UtcNow,
                RowVersion = []
            });
        }

        db.PlayerIdentities.Add(new PlayerIdentity
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            Issuer = issuer,
            Subject = subject,
            Provider = "oidc",
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        });
        await db.SaveChangesAsync();
    }
}
