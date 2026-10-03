using FluentAssertions;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves the resolver's exact Issuer + Subject matching on real SQL Server, where a default
/// (case-insensitive) collation would otherwise fold case. Relies on the binary collation from
/// PlayerIdentityConfiguration; uniqueness itself is covered by PlayerIdentityUniqueIndexIntegrationTests.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class CurrentPlayerResolverSqlServerIntegrationTests : IAsyncLifetime
{
    private const string Issuer = "https://accounts.example.com";

    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public CurrentPlayerResolverSqlServerIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "resolver");
        await _db.MigrateToAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task ExactIssuerAndSubject_Resolve_AndVariantsDoNot()
    {
        var playerId = Guid.NewGuid();
        await using (var context = _db.CreateContext())
        {
            context.Players.Add(new Player { Id = playerId, Username = $"player_{Guid.NewGuid():N}" });
            context.PlayerIdentities.Add(new PlayerIdentity
            {
                Id = Guid.NewGuid(),
                PlayerId = playerId,
                Issuer = Issuer,
                Subject = "AbC123",
                Provider = "oidc"
            });
            await context.SaveChangesAsync();
        }

        (await ResolveAsync(Issuer, "AbC123")).Should().Be(playerId);

        (await ResolveAsync("https://ACCOUNTS.example.com", "AbC123")).Should().BeNull("issuer case is not folded");
        (await ResolveAsync(Issuer + "/", "AbC123")).Should().BeNull("a trailing slash is a different issuer");
        (await ResolveAsync(Issuer, "abc123")).Should().BeNull("subject case is not folded");
        // SQL Server '=' ignores trailing spaces (ANSI padding); the resolver must not.
        (await ResolveAsync(Issuer, "AbC123 ")).Should().BeNull("trailing whitespace is not trimmed");
        (await ResolveAsync(Issuer + " ", "AbC123")).Should().BeNull("trailing whitespace is not trimmed");
    }

    private async Task<Guid?> ResolveAsync(string issuer, string subject)
    {
        await using var context = _db.CreateContext();
        var accessor = new FixedExternalIdentityAccessor(new ExternalIdentity(issuer, subject, "oidc", null));
        return await new CurrentPlayerResolver(accessor, context).ResolvePlayerIdAsync();
    }

    private sealed class FixedExternalIdentityAccessor(ExternalIdentity? identity) : IExternalIdentityAccessor
    {
        public ExternalIdentity? Current { get; } = identity;
    }
}
