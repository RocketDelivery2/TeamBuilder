using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves the EnforceUniquePendingJoinRequest migration's duplicate-data preflight check works
/// against a real historical database: seeded on the schema exactly as it existed after the
/// TeamMember uniqueness migration but before this one (so the pending-join-request unique
/// index does not exist yet and duplicate pending rows can be inserted normally), then applying
/// the migration must fail loudly instead of silently altering data or reporting the index as
/// installed.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class JoinRequestMigrationPreflightIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public JoinRequestMigrationPreflightIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "jrpreflight");
        // Migrate through the TeamMember uniqueness migration but before
        // EnforceUniquePendingJoinRequest, so the pending unique index does not exist yet and
        // duplicate pending rows can be seeded using the actual historical schema.
        await _db.MigrateToAsync(MigrationIds.EnforceUniqueTeamMembership);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task ApplyingMigration_WithDuplicatePendingJoinRequestRows_FailsWithClearPreflightMessage()
    {
        var (teamId, playerId) = await SeedTeamAndPlayerAsync();
        await SeedDuplicatePendingJoinRequestsAsync(teamId, playerId);

        var act = async () => await _db.MigrateToAsync(MigrationIds.EnforceUniquePendingJoinRequest);

        var assertion = await act.Should().ThrowAsync<Exception>();
        var sqlException = FindSqlException(assertion.Which);

        sqlException.Should().NotBeNull("the preflight check raises a real SQL Server error (RAISERROR)");
        sqlException!.Message.Should().Contain("Cannot apply migration EnforceUniquePendingJoinRequest");
        sqlException.Message.Should().Contain("duplicate pending JoinRequest rows");
    }

    [Fact]
    public async Task FailedMigration_DoesNotAlterExistingJoinRequestRows()
    {
        var (teamId, playerId) = await SeedTeamAndPlayerAsync();
        await SeedDuplicatePendingJoinRequestsAsync(teamId, playerId);

        var act = async () => await _db.MigrateToAsync(MigrationIds.EnforceUniquePendingJoinRequest);
        await act.Should().ThrowAsync<Exception>();

        await using var context = _db.CreateContext();
        var duplicatePendingCount = await context.JoinRequests
            .CountAsync(jr => jr.TeamId == teamId && jr.PlayerId == playerId && jr.Status == RequestStatus.Pending);

        duplicatePendingCount.Should().Be(2,
            "the preflight must fail before approving, rejecting, cancelling, deleting, merging, or rewriting any row");
    }

    [Fact]
    public async Task FailedMigration_DoesNotFalselyReportThePendingUniqueIndexAsInstalled()
    {
        var (teamId, playerId) = await SeedTeamAndPlayerAsync();
        await SeedDuplicatePendingJoinRequestsAsync(teamId, playerId);

        var act = async () => await _db.MigrateToAsync(MigrationIds.EnforceUniquePendingJoinRequest);
        await act.Should().ThrowAsync<Exception>();

        var indexExists = await IndexExistsAsync("JoinRequests", "UX_JoinRequests_TeamId_PlayerId_Pending");
        indexExists.Should().BeFalse("the migration failed before CreateIndex ran");

        await using var context = _db.CreateContext();
        var appliedMigrations = await context.Database.GetAppliedMigrationsAsync();
        appliedMigrations.Should().NotContain(MigrationIds.EnforceUniquePendingJoinRequest,
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
    /// Inserts two pending JoinRequest rows for the same (TeamId, PlayerId) pair using the
    /// actual historical (pre-pending-uniqueness) schema. This succeeds because, at this point
    /// in migration history, the pending unique index does not exist yet.
    /// </summary>
    private async Task SeedDuplicatePendingJoinRequestsAsync(Guid teamId, Guid playerId)
    {
        await using var context = _db.CreateContext();

        context.JoinRequests.Add(new JoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            PlayerId = playerId,
            Status = RequestStatus.Pending,
            RequestedAtUtc = DateTime.UtcNow
        });
        context.JoinRequests.Add(new JoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            PlayerId = playerId,
            Status = RequestStatus.Pending,
            RequestedAtUtc = DateTime.UtcNow
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
