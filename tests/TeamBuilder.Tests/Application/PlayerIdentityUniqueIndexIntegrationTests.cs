using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data.Configurations;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves against a real SQL Server that an external identity (Issuer + Subject) can map to
/// at most one TeamBuilder player, while one player may hold many identities.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class PlayerIdentityUniqueIndexIntegrationTests : IAsyncLifetime
{
    private const string EntraIssuer = "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0";
    private const string OtherIssuer = "https://accounts.example.com";

    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public PlayerIdentityUniqueIndexIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "pidindex");
        await _db.MigrateToAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task MultipleDifferentIdentities_ForOnePlayer_AreAllowed()
    {
        var playerId = await SeedPlayerAsync();

        await using (var context = _db.CreateContext())
        {
            context.PlayerIdentities.Add(NewIdentity(playerId, EntraIssuer, Guid.NewGuid().ToString()));
            context.PlayerIdentities.Add(NewIdentity(playerId, OtherIssuer, "user-123"));
            await context.SaveChangesAsync();
        }

        await using var verify = _db.CreateContext();
        (await verify.PlayerIdentities.CountAsync(pi => pi.PlayerId == playerId)).Should().Be(2);
    }

    [Fact]
    public async Task SameSubject_UnderDifferentIssuers_IsAllowed()
    {
        var firstPlayerId = await SeedPlayerAsync();
        var secondPlayerId = await SeedPlayerAsync();
        var subject = Guid.NewGuid().ToString();

        await using var context = _db.CreateContext();
        context.PlayerIdentities.Add(NewIdentity(firstPlayerId, EntraIssuer, subject));
        context.PlayerIdentities.Add(NewIdentity(secondPlayerId, OtherIssuer, subject));

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task SameIssuerAndSubject_ForDifferentPlayers_FailsAtSqlServer()
    {
        var firstPlayerId = await SeedPlayerAsync();
        var secondPlayerId = await SeedPlayerAsync();
        var subject = Guid.NewGuid().ToString();

        await using (var first = _db.CreateContext())
        {
            first.PlayerIdentities.Add(NewIdentity(firstPlayerId, EntraIssuer, subject));
            await first.SaveChangesAsync();
        }

        // An independent context has no tracked knowledge of the first row, so only the real
        // unique index can reject the duplicate. Provider and tenant differ on purpose: they
        // are metadata and must not weaken the Issuer + Subject invariant.
        await using var second = _db.CreateContext();
        second.PlayerIdentities.Add(NewIdentity(secondPlayerId, EntraIssuer, subject, provider: "other", tenantId: "other-tenant"));

        var act = async () => await second.SaveChangesAsync();

        var assertion = await act.Should().ThrowAsync<DbUpdateException>();
        var sqlException = assertion.Which.InnerException as SqlException;
        sqlException.Should().NotBeNull("the failure must be a real SQL Server duplicate-key error");
        sqlException!.Number.Should().BeOneOf(2601, 2627);
        sqlException.Message.Should().Contain(PlayerIdentityConfiguration.IssuerSubjectIndexName);
    }

    [Fact]
    public async Task SameIssuerAndSubject_ForSamePlayer_FailsAtSqlServer()
    {
        var playerId = await SeedPlayerAsync();
        var subject = Guid.NewGuid().ToString();

        await using (var first = _db.CreateContext())
        {
            first.PlayerIdentities.Add(NewIdentity(playerId, EntraIssuer, subject));
            await first.SaveChangesAsync();
        }

        await using var second = _db.CreateContext();
        second.PlayerIdentities.Add(NewIdentity(playerId, EntraIssuer, subject));

        var act = async () => await second.SaveChangesAsync();

        var assertion = await act.Should().ThrowAsync<DbUpdateException>();
        (assertion.Which.InnerException as SqlException)!.Number.Should().BeOneOf(2601, 2627);
    }

    [Fact]
    public async Task SubjectsDifferingOnlyByCase_AreDistinctIdentities()
    {
        var firstPlayerId = await SeedPlayerAsync();
        var secondPlayerId = await SeedPlayerAsync();

        await using var context = _db.CreateContext();
        context.PlayerIdentities.Add(NewIdentity(firstPlayerId, OtherIssuer, "AbC123"));
        context.PlayerIdentities.Add(NewIdentity(secondPlayerId, OtherIssuer, "abc123"));

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Identity_ForNonexistentPlayer_IsRejectedByForeignKey()
    {
        await using var context = _db.CreateContext();
        context.PlayerIdentities.Add(NewIdentity(Guid.NewGuid(), EntraIssuer, Guid.NewGuid().ToString()));

        var act = async () => await context.SaveChangesAsync();

        var assertion = await act.Should().ThrowAsync<DbUpdateException>();
        (assertion.Which.InnerException as SqlException)!.Number.Should().Be(547);
    }

    [Fact]
    public async Task DeletingPlayer_RemovesItsIdentities()
    {
        var playerId = await SeedPlayerAsync();

        await using (var context = _db.CreateContext())
        {
            context.PlayerIdentities.Add(NewIdentity(playerId, EntraIssuer, Guid.NewGuid().ToString()));
            await context.SaveChangesAsync();
        }

        await using (var context = _db.CreateContext())
        {
            await context.Players.Where(p => p.Id == playerId).ExecuteDeleteAsync();
        }

        await using var verify = _db.CreateContext();
        (await verify.PlayerIdentities.AnyAsync(pi => pi.PlayerId == playerId)).Should().BeFalse();
    }

    private async Task<Guid> SeedPlayerAsync()
    {
        await using var context = _db.CreateContext();
        var player = new Player { Id = Guid.NewGuid(), Username = $"player_{Guid.NewGuid():N}" };
        context.Players.Add(player);
        await context.SaveChangesAsync();
        return player.Id;
    }

    private static PlayerIdentity NewIdentity(
        Guid playerId,
        string issuer,
        string subject,
        string provider = "entra",
        string? tenantId = "11111111-1111-1111-1111-111111111111") => new()
    {
        Id = Guid.NewGuid(),
        PlayerId = playerId,
        Issuer = issuer,
        Subject = subject,
        Provider = provider,
        TenantId = tenantId
    };
}
