using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Persistence;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves the UX_TeamMembers_TeamId_PlayerId filtered unique index behaves as intended against
/// a real SQL Server: only one active membership per (TeamId, PlayerId) pair, historical
/// inactive rows never block a rejoin, and other teams/players are unaffected.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class TeamMembershipUniqueIndexIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public TeamMembershipUniqueIndexIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "tmindex");
        await _db.MigrateToAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task FirstActiveMembership_ForPair_Succeeds()
    {
        var (teamId, playerId) = await SeedTeamAndPlayerAsync();

        await using var context = _db.CreateContext();
        context.TeamMembers.Add(NewMember(teamId, playerId, isActive: true));

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task SecondDirectActiveMembership_ForSamePair_FailsAtSqlServer_WithRecognizedConflict()
    {
        var (teamId, playerId) = await SeedTeamAndPlayerAsync();

        await using (var first = _db.CreateContext())
        {
            first.TeamMembers.Add(NewMember(teamId, playerId, isActive: true));
            await first.SaveChangesAsync();
        }

        // A second, independent context simulates a concurrent/direct insert - it has no
        // knowledge of the first context's tracked entity, so only the real unique index
        // (not EF's change tracker) can prevent the duplicate.
        await using var second = _db.CreateContext();
        second.TeamMembers.Add(NewMember(teamId, playerId, isActive: true));

        var act = async () => await second.SaveChangesAsync();

        var assertion = await act.Should().ThrowAsync<DbUpdateException>();
        var dbUpdateException = assertion.Which;

        var sqlException = dbUpdateException.InnerException as SqlException;
        sqlException.Should().NotBeNull("the failure must be a real SQL Server duplicate-key error");
        sqlException!.Number.Should().BeOneOf(2601, 2627);
        sqlException.Message.Should().Contain("UX_TeamMembers_TeamId_PlayerId");

        TeamMembershipConflictClassifier.IsDuplicateTeamMembership(dbUpdateException).Should().BeTrue();
    }

    [Fact]
    public async Task InactiveHistoricalRow_PlusOneActiveRow_ForSamePair_Succeeds()
    {
        var (teamId, playerId) = await SeedTeamAndPlayerAsync();

        await using var context = _db.CreateContext();
        context.TeamMembers.Add(NewMember(teamId, playerId, isActive: false));
        context.TeamMembers.Add(NewMember(teamId, playerId, isActive: true));

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task MultipleInactiveHistoricalRows_ForSamePair_ArePermitted()
    {
        var (teamId, playerId) = await SeedTeamAndPlayerAsync();

        await using var context = _db.CreateContext();
        context.TeamMembers.Add(NewMember(teamId, playerId, isActive: false));
        context.TeamMembers.Add(NewMember(teamId, playerId, isActive: false));
        context.TeamMembers.Add(NewMember(teamId, playerId, isActive: false));

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task ActiveMembership_ForSamePlayer_OnADifferentTeam_IsNotBlocked()
    {
        var (teamId, playerId) = await SeedTeamAndPlayerAsync();
        var (otherTeamId, _) = await SeedTeamAndPlayerAsync();

        await using var context = _db.CreateContext();
        context.TeamMembers.Add(NewMember(teamId, playerId, isActive: true));
        context.TeamMembers.Add(NewMember(otherTeamId, playerId, isActive: true));

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task ActiveMembership_ForSameTeam_WithADifferentPlayer_IsNotBlocked()
    {
        var (teamId, playerId) = await SeedTeamAndPlayerAsync();
        var (_, otherPlayerId) = await SeedTeamAndPlayerAsync();

        await using var context = _db.CreateContext();
        context.TeamMembers.Add(NewMember(teamId, playerId, isActive: true));
        context.TeamMembers.Add(NewMember(teamId, otherPlayerId, isActive: true));

        await context.SaveChangesAsync();
    }

    private async Task<(Guid TeamId, Guid PlayerId)> SeedTeamAndPlayerAsync()
    {
        await using var context = _db.CreateContext();

        var player = new Player { Id = Guid.NewGuid(), Username = $"player_{Guid.NewGuid():N}" };
        var team = new Team { Id = Guid.NewGuid(), Name = $"team_{Guid.NewGuid():N}", MaxMembers = 100 };

        context.Players.Add(player);
        context.Teams.Add(team);
        await context.SaveChangesAsync();

        return (team.Id, player.Id);
    }

    private static TeamMember NewMember(Guid teamId, Guid playerId, bool isActive) => new()
    {
        Id = Guid.NewGuid(),
        TeamId = teamId,
        PlayerId = playerId,
        Role = TeamRole.Member,
        JoinedAtUtc = DateTime.UtcNow,
        IsActive = isActive
    };
}
