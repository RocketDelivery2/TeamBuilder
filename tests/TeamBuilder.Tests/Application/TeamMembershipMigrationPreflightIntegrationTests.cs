using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves the EnforceUniqueTeamMembership migration's duplicate-data preflight check works
/// against a real historical database: seeded on the schema exactly as it existed immediately
/// before this migration (so the unique index does not exist yet and duplicate active rows
/// can be inserted normally), then applying the migration must fail loudly instead of
/// silently altering data or reporting the index as installed.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class TeamMembershipMigrationPreflightIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public TeamMembershipMigrationPreflightIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "tmpreflight");
        // Migrate only to the migration immediately before EnforceUniqueTeamMembership, so
        // the unique index does not exist yet and duplicate active rows can be seeded using
        // the actual historical (pre-uniqueness) schema.
        await _db.MigrateToAsync(MigrationIds.InitialCreate);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task ApplyingMigration_WithDuplicateActiveTeamMemberRows_FailsWithClearPreflightMessage()
    {
        var (teamId, playerId) = await SeedTeamAndPlayerAsync();
        await SeedDuplicateActiveTeamMembersAsync(teamId, playerId);

        var act = async () => await _db.MigrateToAsync(MigrationIds.EnforceUniqueTeamMembership);

        var assertion = await act.Should().ThrowAsync<Exception>();
        var sqlException = FindSqlException(assertion.Which);

        sqlException.Should().NotBeNull("the preflight check raises a real SQL Server error (RAISERROR)");
        sqlException!.Message.Should().Contain("Cannot apply migration EnforceUniqueTeamMembership");
        sqlException.Message.Should().Contain("duplicate active TeamMember rows");
    }

    [Fact]
    public async Task FailedMigration_DoesNotAlterExistingTeamMemberRows()
    {
        var (teamId, playerId) = await SeedTeamAndPlayerAsync();
        await SeedDuplicateActiveTeamMembersAsync(teamId, playerId);

        var act = async () => await _db.MigrateToAsync(MigrationIds.EnforceUniqueTeamMembership);
        await act.Should().ThrowAsync<Exception>();

        await using var context = _db.CreateContext();
        var duplicateActiveCount = await context.TeamMembers
            .CountAsync(tm => tm.TeamId == teamId && tm.PlayerId == playerId && tm.IsActive);

        duplicateActiveCount.Should().Be(2,
            "the preflight must fail before deleting, merging, deactivating, or rewriting any row");
    }

    [Fact]
    public async Task FailedMigration_DoesNotFalselyReportTheUniqueIndexAsInstalled()
    {
        var (teamId, playerId) = await SeedTeamAndPlayerAsync();
        await SeedDuplicateActiveTeamMembersAsync(teamId, playerId);

        var act = async () => await _db.MigrateToAsync(MigrationIds.EnforceUniqueTeamMembership);
        await act.Should().ThrowAsync<Exception>();

        var indexExists = await IndexExistsAsync("TeamMembers", "UX_TeamMembers_TeamId_PlayerId");
        indexExists.Should().BeFalse("the migration failed before CreateIndex ran");

        await using var context = _db.CreateContext();
        var appliedMigrations = await context.Database.GetAppliedMigrationsAsync();
        appliedMigrations.Should().NotContain(MigrationIds.EnforceUniqueTeamMembership,
            "a failed migration must not be recorded as applied");
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

    /// <summary>
    /// Inserts two active TeamMember rows for the same (TeamId, PlayerId) pair using the
    /// actual historical (pre-uniqueness) schema. This succeeds because, at this point in
    /// migration history, the unique index does not exist yet.
    /// </summary>
    private async Task SeedDuplicateActiveTeamMembersAsync(Guid teamId, Guid playerId)
    {
        await using var context = _db.CreateContext();

        context.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            PlayerId = playerId,
            Role = TeamRole.Member,
            JoinedAtUtc = DateTime.UtcNow,
            IsActive = true
        });
        context.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            PlayerId = playerId,
            Role = TeamRole.Member,
            JoinedAtUtc = DateTime.UtcNow,
            IsActive = true
        });

        await context.SaveChangesAsync();
    }

    private async Task<bool> IndexExistsAsync(string tableName, string indexName)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            WHERE t.name = @tableName AND i.name = @indexName
            """;
        command.Parameters.AddWithValue("@tableName", tableName);
        command.Parameters.AddWithValue("@indexName", indexName);

        var count = (int)(await command.ExecuteScalarAsync())!;
        return count > 0;
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
