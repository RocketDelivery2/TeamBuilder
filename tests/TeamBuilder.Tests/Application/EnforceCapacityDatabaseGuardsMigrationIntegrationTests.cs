using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves the EnforceCapacityDatabaseGuards migration against a real historical database:
/// seeded on the schema exactly as it existed before the migration (no capacity CHECK yet),
/// it reconciles stale-but-valid stored counts to the active TeamMember rows, and refuses to
/// run (changing nothing) when a team actually holds more active members than MaxMembers.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class EnforceCapacityDatabaseGuardsMigrationIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public EnforceCapacityDatabaseGuardsMigrationIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "capmigration");
        await _db.MigrateToAsync(MigrationIds.AddPlayerIdentities);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Migration_ReconcilesStaleStoredCounts_ToActiveMembershipCount()
    {
        // Stale-high and out of bounds: 2 active, 1 inactive, stored 5 > MaxMembers 3.
        var staleHigh = await SeedTeamAsync(maxMembers: 3, storedCount: 5);
        await AddMembersAsync(staleHigh, active: 2, inactive: 1);

        // Stale-low and negative.
        var negative = await SeedTeamAsync(maxMembers: 4, storedCount: -1);
        await AddMembersAsync(negative, active: 1, inactive: 0);

        // Inactive history above MaxMembers does not count against capacity.
        var historyHeavy = await SeedTeamAsync(maxMembers: 1, storedCount: 0, status: TeamStatus.Full);
        await AddMembersAsync(historyHeavy, active: 1, inactive: 3);

        // Already consistent: left as is.
        var consistent = await SeedTeamAsync(maxMembers: 2, storedCount: 2, status: TeamStatus.Full);
        await AddMembersAsync(consistent, active: 2, inactive: 0);

        await _db.MigrateToAsync(MigrationIds.EnforceCapacityDatabaseGuards);

        (await GetTeamAsync(staleHigh)).CurrentMemberCount.Should().Be(2);
        (await GetTeamAsync(negative)).CurrentMemberCount.Should().Be(1);
        var history = await GetTeamAsync(historyHeavy);
        history.CurrentMemberCount.Should().Be(1);
        history.MaxMembers.Should().Be(1);
        var ok = await GetTeamAsync(consistent);
        ok.CurrentMemberCount.Should().Be(2);
        ok.Status.Should().Be(TeamStatus.Full);

        (await CheckConstraintExistsAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Migration_OnEmptyAndConsistentDatabase_Succeeds()
    {
        await _db.MigrateToAsync(MigrationIds.EnforceCapacityDatabaseGuards);

        (await CheckConstraintExistsAsync()).Should().BeTrue();
        await using var context = _db.CreateContext();
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Migration_WhenActiveMembershipExceedsMaxMembers_FailsClearly_AndChangesNothing()
    {
        var overfilled = await SeedTeamAsync(maxMembers: 2, storedCount: 2, status: TeamStatus.Full);
        await AddMembersAsync(overfilled, active: 3, inactive: 0);

        // A stale team the reconciliation would otherwise have fixed: must stay untouched.
        var stale = await SeedTeamAsync(maxMembers: 5, storedCount: 4);
        await AddMembersAsync(stale, active: 1, inactive: 0);

        var act = async () => await _db.MigrateToAsync(MigrationIds.EnforceCapacityDatabaseGuards);

        var assertion = await act.Should().ThrowAsync<Exception>();
        var sqlException = FindSqlException(assertion.Which);
        sqlException.Should().NotBeNull("the preflight raises a real SQL Server error (RAISERROR)");
        sqlException!.Message.Should().Contain("Cannot apply migration EnforceCapacityDatabaseGuards");
        sqlException.Message.Should().Contain("1 team(s) have more active TeamMember rows than MaxMembers");
        sqlException.Message.Should().ContainEquivalentOf(overfilled.ToString());

        var over = await GetTeamAsync(overfilled);
        over.MaxMembers.Should().Be(2, "MaxMembers must never be silently raised");
        over.CurrentMemberCount.Should().Be(2);
        (await CountActiveAsync(overfilled)).Should().Be(3, "no membership may be deactivated");
        (await GetTeamAsync(stale)).CurrentMemberCount.Should().Be(4);

        (await CheckConstraintExistsAsync()).Should().BeFalse();
        await using var context = _db.CreateContext();
        (await context.Database.GetAppliedMigrationsAsync())
            .Should().NotContain(MigrationIds.EnforceCapacityDatabaseGuards);
    }

    private async Task<Guid> SeedTeamAsync(int maxMembers, int storedCount, TeamStatus status = TeamStatus.Recruiting)
    {
        await using var context = _db.CreateContext();
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = $"team_{Guid.NewGuid():N}",
            MaxMembers = maxMembers,
            CurrentMemberCount = storedCount,
            Status = status
        };
        context.Teams.Add(team);
        await context.SaveChangesAsync();
        return team.Id;
    }

    private async Task AddMembersAsync(Guid teamId, int active, int inactive)
    {
        await using var context = _db.CreateContext();
        for (var i = 0; i < active + inactive; i++)
        {
            var player = new Player { Id = Guid.NewGuid(), Username = $"player_{Guid.NewGuid():N}" };
            context.Players.Add(player);
            context.TeamMembers.Add(new TeamMember
            {
                Id = Guid.NewGuid(),
                TeamId = teamId,
                PlayerId = player.Id,
                Role = TeamRole.Member,
                JoinedAtUtc = DateTime.UtcNow,
                IsActive = i < active
            });
        }

        await context.SaveChangesAsync();
    }

    private async Task<Team> GetTeamAsync(Guid teamId)
    {
        await using var context = _db.CreateContext();
        return await context.Teams.AsNoTracking().SingleAsync(t => t.Id == teamId);
    }

    private async Task<int> CountActiveAsync(Guid teamId)
    {
        await using var context = _db.CreateContext();
        return await context.TeamMembers.CountAsync(tm => tm.TeamId == teamId && tm.IsActive);
    }

    private async Task<bool> CheckConstraintExistsAsync()
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sys.check_constraints WHERE [name] = 'CK_Teams_CurrentMemberCount_Bounds'";
        return (int)(await command.ExecuteScalarAsync())! > 0;
    }

    private static SqlException? FindSqlException(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException)
            {
                return sqlException;
            }
        }

        return null;
    }
}
