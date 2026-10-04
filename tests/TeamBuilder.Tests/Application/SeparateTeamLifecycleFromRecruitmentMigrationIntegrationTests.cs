using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves the SeparateTeamLifecycleFromRecruitment migration against a real SQL Server database
/// seeded on the exact pre-migration schema (persisted legacy Teams.Status): the explicit
/// backfill, the dropped column and index, the new discovery index, the untouched capacity
/// CHECK, the semantic Down, and that the model has no pending changes.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class SeparateTeamLifecycleFromRecruitmentMigrationIntegrationTests : IAsyncLifetime
{
    private const string NewIndexName = "IX_Teams_LifecycleStatus_IsAcceptingMembers";

    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public SeparateTeamLifecycleFromRecruitmentMigrationIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "lifecyclemig");
        await _db.MigrateToAsync(MigrationIds.EnforceCapacityDatabaseGuards);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Theory]
    [InlineData(TeamStatus.Active, TeamLifecycleStatus.Active, false)]
    [InlineData(TeamStatus.Recruiting, TeamLifecycleStatus.Active, true)]
    [InlineData(TeamStatus.Full, TeamLifecycleStatus.Active, true)]
    [InlineData(TeamStatus.Inactive, TeamLifecycleStatus.Inactive, false)]
    [InlineData(TeamStatus.Disbanded, TeamLifecycleStatus.Disbanded, false)]
    public async Task Up_BackfillsFromLegacyStatus(TeamStatus legacy, TeamLifecycleStatus lifecycle, bool accepting)
    {
        var teamId = await LegacyTeamsTable.InsertAsync(_db.ConnectionString, maxMembers: 4, storedCount: 2, status: legacy);

        await _db.MigrateToAsync(MigrationIds.SeparateTeamLifecycleFromRecruitment);

        await using var context = _db.CreateContext();
        var team = await context.Teams.AsNoTracking().SingleAsync(t => t.Id == teamId);
        team.LifecycleStatus.Should().Be(lifecycle);
        team.IsAcceptingMembers.Should().Be(accepting);
        team.CurrentMemberCount.Should().Be(2);
        team.MaxMembers.Should().Be(4);
    }

    [Fact]
    public async Task Up_BackfillsEveryLegacyValueInOneDatabase()
    {
        var ids = new Dictionary<TeamStatus, Guid>();
        foreach (var legacy in Enum.GetValues<TeamStatus>())
            ids[legacy] = await LegacyTeamsTable.InsertAsync(_db.ConnectionString, maxMembers: 3, storedCount: 3, status: legacy);

        await _db.MigrateToAsync(MigrationIds.SeparateTeamLifecycleFromRecruitment);

        await using var context = _db.CreateContext();
        var rows = await context.Teams.AsNoTracking()
            .Where(t => ids.Values.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => (t.LifecycleStatus, t.IsAcceptingMembers));

        rows[ids[TeamStatus.Active]].Should().Be((TeamLifecycleStatus.Active, false));
        rows[ids[TeamStatus.Recruiting]].Should().Be((TeamLifecycleStatus.Active, true));
        rows[ids[TeamStatus.Full]].Should().Be((TeamLifecycleStatus.Active, true));
        rows[ids[TeamStatus.Inactive]].Should().Be((TeamLifecycleStatus.Inactive, false));
        rows[ids[TeamStatus.Disbanded]].Should().Be((TeamLifecycleStatus.Disbanded, false));
    }

    [Fact]
    public async Task Up_DropsLegacyColumnAndIndex_CreatesDiscoveryIndex_KeepsCapacityCheck()
    {
        await _db.MigrateToAsync(MigrationIds.SeparateTeamLifecycleFromRecruitment);

        (await ColumnExistsAsync("Status")).Should().BeFalse();
        (await ColumnExistsAsync("LifecycleStatus")).Should().BeTrue();
        (await ColumnExistsAsync("IsAcceptingMembers")).Should().BeTrue();
        (await IndexExistsAsync("IX_Teams_Status")).Should().BeFalse();
        (await IndexKeyColumnsAsync(NewIndexName)).Should().Equal("LifecycleStatus", "IsAcceptingMembers");

        var definition = await ScalarAsync<string>(
            "SELECT [definition] FROM sys.check_constraints WHERE [name] = 'CK_Teams_CurrentMemberCount_Bounds'");
        definition.Should().NotBeNull();
        new string(definition!.Where(c => !char.IsWhiteSpace(c) && c != '(' && c != ')').ToArray()).ToLowerInvariant()
            .Should().Be("[currentmembercount]>=0and[currentmembercount]<=[maxmembers]");
    }

    [Fact]
    public async Task Up_WithUnknownLegacyStatus_FailsClearlyAndChangesNothing()
    {
        var unknown = await LegacyTeamsTable.InsertRawStatusAsync(_db.ConnectionString, maxMembers: 3, storedCount: 0, status: 9);
        var known = await LegacyTeamsTable.InsertAsync(_db.ConnectionString, maxMembers: 3, storedCount: 0, status: TeamStatus.Recruiting);

        var act = () => _db.MigrateToAsync(MigrationIds.SeparateTeamLifecycleFromRecruitment);

        var sqlException = FindSqlException((await act.Should().ThrowAsync<Exception>()).Which);
        sqlException.Should().NotBeNull();
        sqlException!.Message.Should().Contain("Cannot apply migration SeparateTeamLifecycleFromRecruitment");
        sqlException.Message.Should().ContainEquivalentOf(unknown.ToString());

        (await ColumnExistsAsync("Status")).Should().BeTrue();
        (await ColumnExistsAsync("LifecycleStatus")).Should().BeFalse();
        (await LegacyTeamsTable.GetAsync(_db.ConnectionString, known)).Status.Should().Be((int)TeamStatus.Recruiting);
        await using var context = _db.CreateContext();
        (await context.Database.GetAppliedMigrationsAsync())
            .Should().NotContain(MigrationIds.SeparateTeamLifecycleFromRecruitment);
    }

    [Fact]
    public async Task Down_ReconstructsLegacyStatusDeterministically()
    {
        await _db.MigrateToAsync(MigrationIds.SeparateTeamLifecycleFromRecruitment);

        var inactive = await InsertCurrentAsync(TeamLifecycleStatus.Inactive, false, count: 3, max: 3);
        var disbanded = await InsertCurrentAsync(TeamLifecycleStatus.Disbanded, false, count: 0, max: 3);
        var acceptingFull = await InsertCurrentAsync(TeamLifecycleStatus.Active, true, count: 3, max: 3);
        var closedFull = await InsertCurrentAsync(TeamLifecycleStatus.Active, false, count: 2, max: 2);
        var recruiting = await InsertCurrentAsync(TeamLifecycleStatus.Active, true, count: 1, max: 3);
        var closedOpen = await InsertCurrentAsync(TeamLifecycleStatus.Active, false, count: 1, max: 3);

        await _db.MigrateToAsync(MigrationIds.EnforceCapacityDatabaseGuards);

        (await ColumnExistsAsync("LifecycleStatus")).Should().BeFalse();
        (await ColumnExistsAsync("IsAcceptingMembers")).Should().BeFalse();
        (await IndexExistsAsync(NewIndexName)).Should().BeFalse();
        (await IndexKeyColumnsAsync("IX_Teams_Status")).Should().Equal("Status");

        async Task<int> StatusOf(Guid id) => (await LegacyTeamsTable.GetAsync(_db.ConnectionString, id)).Status;
        (await StatusOf(inactive)).Should().Be((int)TeamStatus.Inactive);
        (await StatusOf(disbanded)).Should().Be((int)TeamStatus.Disbanded);
        (await StatusOf(acceptingFull)).Should().Be((int)TeamStatus.Full);
        (await StatusOf(closedFull)).Should().Be((int)TeamStatus.Full);
        (await StatusOf(recruiting)).Should().Be((int)TeamStatus.Recruiting);
        (await StatusOf(closedOpen)).Should().Be((int)TeamStatus.Active);
    }

    [Fact]
    public async Task Down_IsSemantic_StaleLegacyFullUnderCapacityComesBackAsRecruiting_AndUpAgainRoundTrips()
    {
        // A historically stale "Full" on a team that is no longer at capacity.
        var stale = await LegacyTeamsTable.InsertAsync(_db.ConnectionString, maxMembers: 5, storedCount: 2, status: TeamStatus.Full);
        var paused = await LegacyTeamsTable.InsertAsync(_db.ConnectionString, maxMembers: 5, storedCount: 2, status: TeamStatus.Active);

        await _db.MigrateToAsync(MigrationIds.SeparateTeamLifecycleFromRecruitment);
        await _db.MigrateToAsync(MigrationIds.EnforceCapacityDatabaseGuards);

        (await LegacyTeamsTable.GetAsync(_db.ConnectionString, stale)).Status.Should().Be((int)TeamStatus.Recruiting);
        (await LegacyTeamsTable.GetAsync(_db.ConnectionString, paused)).Status.Should().Be((int)TeamStatus.Active);

        await _db.MigrateToAsync(MigrationIds.SeparateTeamLifecycleFromRecruitment);

        await using var context = _db.CreateContext();
        var staleTeam = await context.Teams.AsNoTracking().SingleAsync(t => t.Id == stale);
        staleTeam.LifecycleStatus.Should().Be(TeamLifecycleStatus.Active);
        staleTeam.IsAcceptingMembers.Should().BeTrue();
        var pausedTeam = await context.Teams.AsNoTracking().SingleAsync(t => t.Id == paused);
        pausedTeam.IsAcceptingMembers.Should().BeFalse();
    }

    [Fact]
    public async Task FullyMigratedDatabase_HasNoPendingModelChanges()
    {
        await _db.MigrateToAsync();

        await using var context = _db.CreateContext();
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<Guid> InsertCurrentAsync(TeamLifecycleStatus lifecycle, bool accepting, int count, int max)
    {
        var id = Guid.NewGuid();
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO [Teams] ([Id], [Name], [LifecycleStatus], [IsAcceptingMembers], [MaxMembers], [CurrentMemberCount], [CreatedAtUtc])
            VALUES (@id, @name, @lifecycle, @accepting, @max, @count, SYSUTCDATETIME());
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@name", $"team_{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("@lifecycle", (int)lifecycle);
        command.Parameters.AddWithValue("@accepting", accepting);
        command.Parameters.AddWithValue("@max", max);
        command.Parameters.AddWithValue("@count", count);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<bool> ColumnExistsAsync(string column) =>
        await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[Teams]') AND [name] = N'{column}'") > 0;

    private async Task<bool> IndexExistsAsync(string index) =>
        await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[Teams]') AND [name] = N'{index}'") > 0;

    private async Task<List<string>> IndexKeyColumnsAsync(string index)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.[name]
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
            JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
            WHERE i.[object_id] = OBJECT_ID(N'[Teams]') AND i.[name] = @name AND ic.[is_included_column] = 0
            ORDER BY ic.[key_ordinal];
            """;
        command.Parameters.AddWithValue("@name", index);
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));
        return columns;
    }

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private static SqlException? FindSqlException(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException)
                return sqlException;
        }

        return null;
    }
}
