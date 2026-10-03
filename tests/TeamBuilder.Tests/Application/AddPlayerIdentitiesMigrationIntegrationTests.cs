using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves the AddPlayerIdentities migration is additive: a database populated on the schema
/// immediately before it keeps its existing players, teams and memberships unchanged.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class AddPlayerIdentitiesMigrationIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public AddPlayerIdentitiesMigrationIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "pidmigration");
        await _db.MigrateToAsync(MigrationIds.EnforceUniquePendingJoinRequest);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task ApplyingMigration_PreservesExistingPlayersTeamsAndMemberships()
    {
        var player = new Player { Id = Guid.NewGuid(), Username = $"player_{Guid.NewGuid():N}", Email = "p@example.com" };
        var team = new Team { Id = Guid.NewGuid(), Name = $"team_{Guid.NewGuid():N}", MaxMembers = 10, CurrentMemberCount = 1, OwnerId = player.Id };
        var membership = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            PlayerId = player.Id,
            Role = TeamRole.Member,
            JoinedAtUtc = DateTime.UtcNow,
            IsActive = true
        };

        await using (var seed = _db.CreateContext())
        {
            seed.Players.Add(player);
            seed.Teams.Add(team);
            seed.TeamMembers.Add(membership);
            await seed.SaveChangesAsync();
        }

        await _db.MigrateToAsync(MigrationIds.AddPlayerIdentities);

        await using var verify = _db.CreateContext();
        (await verify.Database.GetAppliedMigrationsAsync()).Should().Contain(MigrationIds.AddPlayerIdentities);

        var storedPlayer = await verify.Players.SingleAsync(p => p.Id == player.Id);
        storedPlayer.Username.Should().Be(player.Username);
        storedPlayer.Email.Should().Be("p@example.com");

        var storedTeam = await verify.Teams.SingleAsync(t => t.Id == team.Id);
        storedTeam.OwnerId.Should().Be(player.Id);
        storedTeam.CurrentMemberCount.Should().Be(1);

        (await verify.TeamMembers.CountAsync(tm => tm.PlayerId == player.Id && tm.IsActive)).Should().Be(1);
        (await verify.PlayerIdentities.CountAsync()).Should().Be(0);
    }
}
