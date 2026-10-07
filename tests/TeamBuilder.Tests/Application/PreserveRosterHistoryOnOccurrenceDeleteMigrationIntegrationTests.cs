using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves the PreserveRosterHistoryOnOccurrenceDelete migration on a real SQL Server database:
/// Up turns FK_RosterAssignments_Events_OccurrenceId from CASCADE into NO ACTION without
/// touching existing rows, so a raw DELETE of an occurrence with participation history is
/// refused by the database itself; Down restores the cascade.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class PreserveRosterHistoryOnOccurrenceDeleteMigrationIntegrationTests : IAsyncLifetime
{
    private const string ForeignKey = "FK_RosterAssignments_Events_OccurrenceId";

    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public PreserveRosterHistoryOnOccurrenceDeleteMigrationIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "historymig");
        await _db.MigrateToAsync(MigrationIds.AddEventRosterRequirementsAndAssignments);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Before_TheOccurrenceCascadeErasesAssignmentHistory()
    {
        // The defect this migration fixes, reproduced on the #172 schema.
        (await DeleteActionAsync()).Should().Be("CASCADE");
        var eventId = await SeedEventWithHistoryAsync();

        await ExecuteAsync($"DELETE FROM [Events] WHERE [Id] = '{eventId}'");

        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [RosterAssignments] WHERE [OccurrenceId] = '{eventId}'")).Should().Be(0);
    }

    [Fact]
    public async Task Up_MakesTheForeignKeyNoAction_KeepsRows_AndRefusesARawDelete()
    {
        var eventId = await SeedEventWithHistoryAsync();

        await _db.MigrateToAsync(MigrationIds.PreserveRosterHistoryOnOccurrenceDelete);

        (await DeleteActionAsync()).Should().Be("NO_ACTION");
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [RosterAssignments] WHERE [OccurrenceId] = '{eventId}'")).Should().Be(2);

        var act = () => ExecuteAsync($"DELETE FROM [Events] WHERE [Id] = '{eventId}'");
        (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(547);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [Events] WHERE [Id] = '{eventId}'")).Should().Be(1);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [RosterAssignments] WHERE [OccurrenceId] = '{eventId}'")).Should().Be(2);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [RosterRequirements] WHERE [OccurrenceId] = '{eventId}'")).Should().Be(1);
    }

    [Fact]
    public async Task Up_StillLetsAnOccurrenceWithoutAssignmentsGo_WithItsRequirements()
    {
        var eventId = await SeedEventAsync();
        await ExecuteAsync($"""
            INSERT INTO [RosterRequirements] ([Id], [OccurrenceId], [RoleCode], [RequiredCount], [CreatedAtUtc])
            VALUES (NEWID(), '{eventId}', N'participant', 10, SYSUTCDATETIME());
            """);
        await _db.MigrateToAsync(MigrationIds.PreserveRosterHistoryOnOccurrenceDelete);

        await ExecuteAsync($"DELETE FROM [Events] WHERE [Id] = '{eventId}'");

        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [RosterRequirements] WHERE [OccurrenceId] = '{eventId}'")).Should().Be(0);
    }

    [Fact]
    public async Task Down_RestoresTheCascade()
    {
        await _db.MigrateToAsync(MigrationIds.PreserveRosterHistoryOnOccurrenceDelete);

        await _db.MigrateToAsync(MigrationIds.AddEventRosterRequirementsAndAssignments);

        (await DeleteActionAsync()).Should().Be("CASCADE");
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

    private async Task<string?> DeleteActionAsync() =>
        await ScalarAsync<string>($"SELECT [delete_referential_action_desc] FROM sys.foreign_keys WHERE [name] = N'{ForeignKey}'");

    private async Task<Guid> SeedEventAsync()
    {
        var eventId = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO [Events] ([Id], [Name], [ScheduledStartUtc], [Status], [MaxParticipants], [CurrentParticipantCount], [IsDetached], [CreatedAtUtc])
            VALUES ('{eventId}', N'Pickup', '2026-10-20T22:00:00', 2, 10, 0, 0, SYSUTCDATETIME());
            """);
        return eventId;
    }

    /// <summary>An occurrence with one requirement, a departed row and its replacement.</summary>
    private async Task<Guid> SeedEventWithHistoryAsync()
    {
        var eventId = await SeedEventAsync();
        var requirementId = Guid.NewGuid();
        var departedId = Guid.NewGuid();
        var leaver = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO [Players] ([Id], [Username], [CreatedAtUtc]) VALUES
                ('{leaver}', N'leaver-{Guid.NewGuid():N}', SYSUTCDATETIME()),
                ('{replacement}', N'sub-{Guid.NewGuid():N}', SYSUTCDATETIME());
            INSERT INTO [RosterRequirements] ([Id], [OccurrenceId], [RoleCode], [RequiredCount], [CreatedAtUtc])
            VALUES ('{requirementId}', '{eventId}', N'participant', 10, SYSUTCDATETIME());
            INSERT INTO [RosterAssignments] ([Id], [OccurrenceId], [PlayerId], [RequirementId], [Status], [Source], [ExitReason], [CreatedAtUtc])
            VALUES ('{departedId}', '{eventId}', '{leaver}', '{requirementId}', 5, 2, 1, SYSUTCDATETIME());
            INSERT INTO [RosterAssignments] ([Id], [OccurrenceId], [PlayerId], [RequirementId], [Status], [Source], [ReplacedAssignmentId], [CreatedAtUtc])
            VALUES (NEWID(), '{eventId}', '{replacement}', '{requirementId}', 4, 2, '{departedId}', SYSUTCDATETIME());
            """);
        return eventId;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
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
}
