using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves the database-boundary capacity guards against a real SQL Server: the Team owner and
/// TeamMember player foreign keys refuse a Player delete (NO ACTION) instead of silently
/// nulling the owner or cascading memberships away, PlayerService.DeleteAsync deletes
/// memberships explicitly and maps only the expected late-reference races to a 409, the
/// CK_Teams_CurrentMemberCount_Bounds check bounds the stored count, and the capacity indexes
/// exist.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class CapacityDatabaseGuardsSqlServerIntegrationTests : IAsyncLifetime
{
    private const string OwnedTeamsMessage = "Delete or transfer ownership of all owned teams before deleting this player.";
    private const string LateMembershipMessage = "This player joined a team while being deleted. Please try again.";
    private const string CheckName = "CK_Teams_CurrentMemberCount_Bounds";

    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public CapacityDatabaseGuardsSqlServerIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "capguards");
        await _db.MigrateToAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ── Foreign keys: direct database deletes ───────────────────────────────

    [Theory]
    [InlineData("FK_Teams_Players_OwnerId", "Teams")]
    [InlineData("FK_TeamMembers_Players_PlayerId", "TeamMembers")]
    public async Task PlayerForeignKeys_AreNoAction(string foreignKeyName, string tableName)
    {
        var (table, deleteAction) = await GetForeignKeyAsync(foreignKeyName);

        table.Should().Be(tableName);
        deleteAction.Should().Be("NO_ACTION");
    }

    [Fact]
    public async Task Database_RefusesDirectPlayerDelete_WhilePlayerOwnsATeam_AndNeverNullsOwner()
    {
        var ownerId = await SeedPlayerAsync();
        var teamId = await SeedTeamAsync(maxMembers: 5, storedCount: 0, ownerId: ownerId);

        var act = () => ExecuteSqlAsync("DELETE FROM [Players] WHERE [Id] = @id", ("@id", ownerId));

        var ex = (await act.Should().ThrowAsync<SqlException>()).Which;
        ex.Number.Should().Be(547);
        ex.Message.Should().Contain("FK_Teams_Players_OwnerId");

        (await PlayerExistsAsync(ownerId)).Should().BeTrue();
        (await GetTeamAsync(teamId)).OwnerId.Should().Be(ownerId, "the owner must never be silently set null");
    }

    [Fact]
    public async Task Database_RefusesDirectPlayerDelete_WhileTeamMemberReferencesPlayer()
    {
        var playerId = await SeedPlayerAsync();
        var activeTeam = await SeedTeamAsync(maxMembers: 5, storedCount: 1);
        await AddMembershipAsync(activeTeam, playerId, isActive: true);

        var act = () => ExecuteSqlAsync("DELETE FROM [Players] WHERE [Id] = @id", ("@id", playerId));

        var ex = (await act.Should().ThrowAsync<SqlException>()).Which;
        ex.Number.Should().Be(547);
        ex.Message.Should().Contain("FK_TeamMembers_Players_PlayerId");

        (await PlayerExistsAsync(playerId)).Should().BeTrue();
        (await IsActiveMemberAsync(activeTeam, playerId)).Should().BeTrue("memberships must not be cascaded away");
    }

    [Fact]
    public async Task Database_RefusesDirectPlayerDelete_WhileOnlyInactiveHistoryReferencesPlayer()
    {
        var playerId = await SeedPlayerAsync();
        var teamId = await SeedTeamAsync(maxMembers: 5, storedCount: 0);
        await AddMembershipAsync(teamId, playerId, isActive: false);

        var act = () => ExecuteSqlAsync("DELETE FROM [Players] WHERE [Id] = @id", ("@id", playerId));

        (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(547);
        (await CountMembershipRowsAsync(playerId)).Should().Be(1);
    }

    // ── PlayerService.DeleteAsync ────────────────────────────────────────────

    [Fact]
    public async Task DeletePlayer_NonOwner_ExplicitlyRemovesAllMemberships_AndReconcilesAffectedTeams()
    {
        var playerId = await SeedPlayerAsync();

        // Stale-high stored count; 3 active including the player.
        var teamA = await SeedTeamAsync(maxMembers: 10, storedCount: 8);
        await AddMembershipAsync(teamA, playerId, isActive: true);
        await AddActiveMembersAsync(teamA, 2);

        // Stale-low stored count; 4 active including the player.
        var teamB = await SeedTeamAsync(maxMembers: 10, storedCount: 1, status: TeamStatus.Active);
        await AddMembershipAsync(teamB, playerId, isActive: true);
        await AddActiveMembersAsync(teamB, 3);

        // Inactive history only: the row is deleted, but the team is not reconciled.
        var historyTeam = await SeedTeamAsync(maxMembers: 5, storedCount: 4, status: TeamStatus.Full);
        await AddMembershipAsync(historyTeam, playerId, isActive: false);

        (await DeletePlayerAsync(playerId)).Should().BeTrue();

        (await PlayerExistsAsync(playerId)).Should().BeFalse();
        (await CountMembershipRowsAsync(playerId)).Should().Be(0, "active and inactive memberships are deleted explicitly");

        (await GetTeamAsync(teamA)).CurrentMemberCount.Should().Be(2);
        (await CountActiveAsync(teamA)).Should().Be(2);
        var b = await GetTeamAsync(teamB);
        b.CurrentMemberCount.Should().Be(3);
        b.Status.Should().Be(TeamStatus.Active);
        (await CountActiveAsync(teamB)).Should().Be(3);

        var history = await GetTeamAsync(historyTeam);
        history.CurrentMemberCount.Should().Be(4);
        history.Status.Should().Be(TeamStatus.Full);
    }

    [Fact]
    public async Task DeletePlayer_WhoFilledATeam_TransitionsFullTeamToRecruiting()
    {
        var playerId = await SeedPlayerAsync();
        var teamId = await SeedTeamAsync(maxMembers: 2, storedCount: 2, status: TeamStatus.Full);
        await AddMembershipAsync(teamId, playerId, isActive: true);
        await AddActiveMembersAsync(teamId, 1);

        (await DeletePlayerAsync(playerId)).Should().BeTrue();

        var team = await GetTeamAsync(teamId);
        team.CurrentMemberCount.Should().Be(1);
        team.Status.Should().Be(TeamStatus.Recruiting);
    }

    [Fact]
    public async Task DeletePlayer_Owner_RemainsDeterministicConflict_AndChangesNothing()
    {
        var ownerId = await SeedPlayerAsync();
        var ownedTeam = await SeedTeamAsync(maxMembers: 5, storedCount: 0, ownerId: ownerId);
        var otherTeam = await SeedTeamAsync(maxMembers: 2, storedCount: 2, status: TeamStatus.Full);
        await AddMembershipAsync(otherTeam, ownerId, isActive: true);
        await AddActiveMembersAsync(otherTeam, 1);

        var act = () => DeletePlayerAsync(ownerId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(OwnedTeamsMessage);

        (await PlayerExistsAsync(ownerId)).Should().BeTrue();
        (await GetTeamAsync(ownedTeam)).OwnerId.Should().Be(ownerId);
        (await IsActiveMemberAsync(otherTeam, ownerId)).Should().BeTrue();
        (await GetTeamAsync(otherTeam)).CurrentMemberCount.Should().Be(2);
    }

    [Fact]
    public async Task DeletePlayer_WhenTeamOwnershipAppearsAfterOwnerCheck_ConflictsInsteadOfOrphaningTheTeam()
    {
        var playerId = await SeedPlayerAsync();
        var memberTeam = await SeedTeamAsync(maxMembers: 2, storedCount: 2, status: TeamStatus.Full);
        await AddMembershipAsync(memberTeam, playerId, isActive: true);
        await AddActiveMembersAsync(memberTeam, 1);

        // The player creates (and so owns) a team after DeleteAsync has passed its owner guard
        // and loaded its memberships, immediately before the delete is saved.
        Guid lateTeamId = default;
        await using var context = CreateInterferingContext(async () =>
        {
            await using var other = _db.CreateContext();
            var created = await new TeamService(other).CreateAsync(
                new CreateTeamDto { Name = $"late_{Guid.NewGuid():N}", MaxMembers = 4 }, playerId);
            lateTeamId = created.Id;
        });

        var act = () => new PlayerService(context).DeleteAsync(playerId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(OwnedTeamsMessage);

        (await PlayerExistsAsync(playerId)).Should().BeTrue();
        (await GetTeamAsync(lateTeamId)).OwnerId.Should().Be(playerId, "the late team must not be silently orphaned");

        // The whole delete rolled back: membership and the member team's correction included.
        (await IsActiveMemberAsync(memberTeam, playerId)).Should().BeTrue();
        var team = await GetTeamAsync(memberTeam);
        team.CurrentMemberCount.Should().Be(2);
        team.Status.Should().Be(TeamStatus.Full);
    }

    [Fact]
    public async Task DeletePlayer_WhenMembershipAppearsAfterMembershipQuery_ConflictsAndRollsBackEverything()
    {
        var playerId = await SeedPlayerAsync();

        var memberTeam = await SeedTeamAsync(maxMembers: 3, storedCount: 3, status: TeamStatus.Full);
        await AddMembershipAsync(memberTeam, playerId, isActive: true);
        await AddActiveMembersAsync(memberTeam, 2);

        var historyTeam = await SeedTeamAsync(maxMembers: 5, storedCount: 0);
        await AddMembershipAsync(historyTeam, playerId, isActive: false);

        // A team the player is not yet in, with a pending request from the player; its owner
        // approves the request after DeleteAsync has loaded the player's memberships. The late
        // team is not one DeleteAsync reconciles, so only the FK can catch this race.
        var lateOwnerId = await SeedPlayerAsync();
        var lateTeam = await SeedTeamAsync(maxMembers: 4, storedCount: 1, ownerId: lateOwnerId);
        await AddActiveMembersAsync(lateTeam, 1);
        var requestId = await SeedPendingRequestAsync(lateTeam, playerId);

        await using var context = CreateInterferingContext(async () =>
        {
            await using var other = _db.CreateContext();
            await new JoinRequestService(other).ProcessAsync(
                requestId, new ProcessJoinRequestDto { Status = RequestStatus.Approved }, lateOwnerId);
        });

        var act = () => new PlayerService(context).DeleteAsync(playerId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(LateMembershipMessage);

        // The late membership was not cascade-deleted and its team's count stays exact.
        (await PlayerExistsAsync(playerId)).Should().BeTrue();
        (await IsActiveMemberAsync(lateTeam, playerId)).Should().BeTrue();
        var late = await GetTeamAsync(lateTeam);
        late.CurrentMemberCount.Should().Be(2);
        (await CountActiveAsync(lateTeam)).Should().Be(2);

        // Rollback: the earlier memberships and the member team's count are untouched.
        (await IsActiveMemberAsync(memberTeam, playerId)).Should().BeTrue();
        (await CountMembershipRowsAsync(playerId)).Should().Be(3);
        var member = await GetTeamAsync(memberTeam);
        member.CurrentMemberCount.Should().Be(3);
        member.Status.Should().Be(TeamStatus.Full);
    }

    [Fact]
    public async Task DeletePlayer_UnrelatedSaveFailure_IsNotMappedToConflict()
    {
        var playerId = await SeedPlayerAsync();
        var teamId = await SeedTeamAsync(maxMembers: 5, storedCount: 1);
        await AddMembershipAsync(teamId, playerId, isActive: true);
        await AddActiveMembersAsync(teamId, 1);

        // A database failure on the Player delete that is not one of the two expected reference
        // races must surface unchanged, never as a friendly 409. Any trigger on Players makes
        // EF Core's DELETE ... OUTPUT fail (SQL error 334), which is exactly such a failure.
        await ExecuteSqlAsync("""
            CREATE TRIGGER [TR_Test_Players_Delete] ON [Players] AFTER DELETE AS
            BEGIN
                THROW 50001, 'unrelated failure', 1;
            END
            """);

        var act = () => DeletePlayerAsync(playerId);

        var ex = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        ex.Should().NotBeOfType<DbUpdateConcurrencyException>();
        ex.InnerException.Should().BeOfType<SqlException>().Which.Number.Should().NotBe(547);
        (await PlayerExistsAsync(playerId)).Should().BeTrue();
        (await IsActiveMemberAsync(teamId, playerId)).Should().BeTrue();
    }

    // ── CK_Teams_CurrentMemberCount_Bounds ───────────────────────────────────

    [Fact]
    public async Task CapacityCheck_IsInstalled_WithExpectedDefinition()
    {
        var definition = await ScalarAsync<string?>("""
            SELECT cc.[definition]
            FROM sys.check_constraints cc
            WHERE cc.[name] = @name AND cc.[parent_object_id] = OBJECT_ID(N'[Teams]')
            """, ("@name", CheckName));

        definition.Should().NotBeNull();
        var normalized = new string(definition!.Where(c => !char.IsWhiteSpace(c) && c != '(' && c != ')').ToArray()).ToLowerInvariant();
        normalized.Should().Be("[currentmembercount]>=0and[currentmembercount]<=[maxmembers]");
    }

    [Fact]
    public async Task CapacityCheck_RejectsNegativeCurrentMemberCount()
    {
        var teamId = await SeedTeamAsync(maxMembers: 3, storedCount: 0);

        var act = () => ExecuteSqlAsync(
            "UPDATE [Teams] SET [CurrentMemberCount] = -1 WHERE [Id] = @id", ("@id", teamId));

        var ex = (await act.Should().ThrowAsync<SqlException>()).Which;
        ex.Number.Should().Be(547);
        ex.Message.Should().Contain(CheckName);
        (await GetTeamAsync(teamId)).CurrentMemberCount.Should().Be(0);
    }

    [Fact]
    public async Task CapacityCheck_RejectsCurrentMemberCountAboveMaxMembers()
    {
        var teamId = await SeedTeamAsync(maxMembers: 3, storedCount: 3);

        var act = () => ExecuteSqlAsync(
            "UPDATE [Teams] SET [CurrentMemberCount] = 4 WHERE [Id] = @id", ("@id", teamId));

        var ex = (await act.Should().ThrowAsync<SqlException>()).Which;
        ex.Number.Should().Be(547);
        ex.Message.Should().Contain(CheckName);
        (await GetTeamAsync(teamId)).CurrentMemberCount.Should().Be(3);
    }

    [Fact]
    public async Task CapacityCheck_RejectsLoweringMaxMembersBelowStoredCount()
    {
        var teamId = await SeedTeamAsync(maxMembers: 3, storedCount: 3);

        var act = () => ExecuteSqlAsync("UPDATE [Teams] SET [MaxMembers] = 2 WHERE [Id] = @id", ("@id", teamId));

        (await act.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain(CheckName);
    }

    [Fact]
    public async Task CapacityCheck_RejectsInsertOutsideBounds_ThroughEfCore()
    {
        var act = () => SeedTeamAsync(maxMembers: 2, storedCount: 3);

        var ex = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        ex.InnerException.Should().BeOfType<SqlException>().Which.Message.Should().Contain(CheckName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task CapacityCheck_AllowsZeroAndExactlyMaxMembers(int count)
    {
        var teamId = await SeedTeamAsync(maxMembers: 3, storedCount: 1);

        await ExecuteSqlAsync(
            "UPDATE [Teams] SET [CurrentMemberCount] = @count WHERE [Id] = @id", ("@count", count), ("@id", teamId));

        (await GetTeamAsync(teamId)).CurrentMemberCount.Should().Be(count);
    }

    // ── Indexes ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("IX_TeamMembers_TeamId_IsActive", "TeamId,IsActive")]
    [InlineData("IX_TeamMembers_PlayerId_IsActive", "PlayerId,IsActive")]
    [InlineData("UX_TeamMembers_TeamId_PlayerId", "TeamId,PlayerId")]
    public async Task CapacityIndexes_ExistWithExpectedKeyColumns(string indexName, string expectedKeyColumns)
    {
        var keyColumns = await ScalarAsync<string?>("""
            SELECT STRING_AGG(c.[name], ',') WITHIN GROUP (ORDER BY ic.[key_ordinal])
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
            JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
            WHERE i.[object_id] = OBJECT_ID(N'[TeamMembers]') AND i.[name] = @name AND ic.[is_included_column] = 0
            """, ("@name", indexName));

        keyColumns.Should().Be(expectedKeyColumns);
    }

    [Theory]
    [InlineData("IX_TeamMembers_TeamId_IsActive")]
    [InlineData("IX_TeamMembers_PlayerId_IsActive")]
    public async Task CapacityIndexes_AreNonUniqueAndUnfiltered_SoTheyAlsoServeForeignKeyChecks(string indexName)
    {
        var (isUnique, hasFilter) = await GetIndexFlagsAsync(indexName);

        isUnique.Should().BeFalse();
        hasFilter.Should().BeFalse();
    }

    [Theory]
    [InlineData("IX_TeamMembers_PlayerId")]
    [InlineData("IX_TeamMembers_IsActive")]
    public async Task SupersededTeamMemberIndexes_AreDropped(string indexName)
    {
        var count = await ScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[TeamMembers]') AND [name] = @name",
            ("@name", indexName));

        count.Should().Be(0);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private TeamBuilderDbContext CreateInterferingContext(Func<Task> beforeFirstSave)
    {
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseSqlServer(_db.ConnectionString)
            .Options;

        return new InterferingTeamBuilderDbContext(options, beforeFirstSave);
    }

    private async Task<Guid> SeedPlayerAsync()
    {
        await using var context = _db.CreateContext();
        var player = new Player { Id = Guid.NewGuid(), Username = $"player_{Guid.NewGuid():N}" };
        context.Players.Add(player);
        await context.SaveChangesAsync();
        return player.Id;
    }

    private async Task<Guid> SeedTeamAsync(
        int maxMembers,
        int storedCount,
        TeamStatus status = TeamStatus.Recruiting,
        Guid? ownerId = null)
    {
        await using var context = _db.CreateContext();
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = $"team_{Guid.NewGuid():N}",
            MaxMembers = maxMembers,
            CurrentMemberCount = storedCount,
            Status = status,
            OwnerId = ownerId
        };
        context.Teams.Add(team);
        await context.SaveChangesAsync();
        return team.Id;
    }

    private async Task AddMembershipAsync(Guid teamId, Guid playerId, bool isActive)
    {
        await using var context = _db.CreateContext();
        context.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            PlayerId = playerId,
            Role = TeamRole.Member,
            JoinedAtUtc = DateTime.UtcNow,
            IsActive = isActive
        });
        await context.SaveChangesAsync();
    }

    private async Task AddActiveMembersAsync(Guid teamId, int count)
    {
        for (var i = 0; i < count; i++)
            await AddMembershipAsync(teamId, await SeedPlayerAsync(), isActive: true);
    }

    private async Task<Guid> SeedPendingRequestAsync(Guid teamId, Guid playerId)
    {
        await using var context = _db.CreateContext();
        var request = new JoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            PlayerId = playerId,
            Status = RequestStatus.Pending,
            RequestedAtUtc = DateTime.UtcNow
        };
        context.JoinRequests.Add(request);
        await context.SaveChangesAsync();
        return request.Id;
    }

    private async Task<bool> DeletePlayerAsync(Guid playerId)
    {
        await using var context = _db.CreateContext();
        return await new PlayerService(context).DeleteAsync(playerId);
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

    private async Task<int> CountMembershipRowsAsync(Guid playerId)
    {
        await using var context = _db.CreateContext();
        return await context.TeamMembers.CountAsync(tm => tm.PlayerId == playerId);
    }

    private async Task<bool> IsActiveMemberAsync(Guid teamId, Guid playerId)
    {
        await using var context = _db.CreateContext();
        return await context.TeamMembers.AnyAsync(tm => tm.TeamId == teamId && tm.PlayerId == playerId && tm.IsActive);
    }

    private async Task<bool> PlayerExistsAsync(Guid playerId)
    {
        await using var context = _db.CreateContext();
        return await context.Players.AnyAsync(p => p.Id == playerId);
    }

    private async Task<(string Table, string DeleteAction)> GetForeignKeyAsync(string foreignKeyName)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT OBJECT_NAME(fk.[parent_object_id]), fk.[delete_referential_action_desc]
            FROM sys.foreign_keys fk
            WHERE fk.[name] = @name AND fk.[referenced_object_id] = OBJECT_ID(N'[Players]')
            """;
        command.Parameters.AddWithValue("@name", foreignKeyName);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"foreign key {foreignKeyName} must exist");
        return (reader.GetString(0), reader.GetString(1));
    }

    private async Task<(bool IsUnique, bool HasFilter)> GetIndexFlagsAsync(string indexName)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.[is_unique], i.[has_filter]
            FROM sys.indexes i
            WHERE i.[object_id] = OBJECT_ID(N'[TeamMembers]') AND i.[name] = @name
            """;
        command.Parameters.AddWithValue("@name", indexName);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"index {indexName} must exist");
        return (reader.GetBoolean(0), reader.GetBoolean(1));
    }

    private async Task ExecuteSqlAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default! : (T)result;
    }

    /// <summary>
    /// Runs a real, independently committed write (through its own context) immediately before
    /// this context's first SaveChanges, so the service under test has already run its queries
    /// when the concurrent change lands.
    /// </summary>
    private sealed class InterferingTeamBuilderDbContext(
        DbContextOptions<TeamBuilderDbContext> options,
        Func<Task> beforeFirstSave) : TeamBuilderDbContext(options)
    {
        private Func<Task>? _beforeFirstSave = beforeFirstSave;

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var interference = _beforeFirstSave;
            _beforeFirstSave = null;
            if (interference is not null)
                await interference();

            return await base.SaveChangesAsync(cancellationToken);
        }
    }
}
