using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves the AddEventRosterRequirementsAndAssignments migration on a real SQL Server database:
/// Up adds the two tables without touching existing events or legacy roster entries, Down
/// removes only them, and the fully migrated model has no pending changes.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class AddEventRosterRequirementsAndAssignmentsMigrationIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public AddEventRosterRequirementsAndAssignmentsMigrationIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "rostermig");
        await _db.MigrateToAsync(MigrationIds.AddEventSeriesMaterializationCheckpoint);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Up_AddsRosterTables_AndLeavesEventsAndLegacyRosterEntriesUntouched()
    {
        var (eventId, playerId) = await SeedEventWithLegacyRosterEntryAsync();

        await _db.MigrateToAsync(MigrationIds.AddEventRosterRequirementsAndAssignments);

        (await TableExistsAsync("RosterRequirements")).Should().BeTrue();
        (await TableExistsAsync("RosterAssignments")).Should().BeTrue();
        (await ScalarAsync<int>($"SELECT [CurrentParticipantCount] FROM [Events] WHERE [Id] = '{eventId}'")).Should().Be(1);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [RosterEntries] WHERE [EventId] = '{eventId}'")).Should().Be(1);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM [RosterAssignments]")).Should().Be(0);

        // The new tables accept the documented shapes on the migrated schema.
        var requirementId = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO [RosterRequirements] ([Id], [OccurrenceId], [RoleCode], [RequiredCount], [CreatedAtUtc])
            VALUES ('{requirementId}', '{eventId}', N'participant', 10, SYSUTCDATETIME());
            INSERT INTO [RosterAssignments] ([Id], [OccurrenceId], [PlayerId], [RequirementId], [Status], [Source], [CreatedAtUtc])
            VALUES (NEWID(), '{eventId}', '{playerId}', '{requirementId}', 2, 2, SYSUTCDATETIME());
            """);
    }

    [Fact]
    public async Task Down_RemovesOnlyTheRosterTables()
    {
        var (eventId, _) = await SeedEventWithLegacyRosterEntryAsync();
        await _db.MigrateToAsync(MigrationIds.AddEventRosterRequirementsAndAssignments);

        await _db.MigrateToAsync(MigrationIds.AddEventSeriesMaterializationCheckpoint);

        (await TableExistsAsync("RosterRequirements")).Should().BeFalse();
        (await TableExistsAsync("RosterAssignments")).Should().BeFalse();
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [Events] WHERE [Id] = '{eventId}'")).Should().Be(1);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [RosterEntries] WHERE [EventId] = '{eventId}'")).Should().Be(1);

        await _db.MigrateToAsync(MigrationIds.AddEventRosterRequirementsAndAssignments);
        (await TableExistsAsync("RosterAssignments")).Should().BeTrue();
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

    private async Task<(Guid EventId, Guid PlayerId)> SeedEventWithLegacyRosterEntryAsync()
    {
        var eventId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO [Players] ([Id], [Username], [CreatedAtUtc])
            VALUES ('{playerId}', N'legacy-{Guid.NewGuid():N}', SYSUTCDATETIME());
            INSERT INTO [Events] ([Id], [Name], [ScheduledStartUtc], [Status], [MaxParticipants], [CurrentParticipantCount], [IsDetached], [HostId], [CreatedAtUtc])
            VALUES ('{eventId}', N'Legacy event', '2026-10-20T22:00:00', 1, 10, 1, 0, '{playerId}', SYSUTCDATETIME());
            INSERT INTO [RosterEntries] ([Id], [EventId], [PlayerId], [IsConfirmed], [RegisteredAtUtc], [CreatedAtUtc])
            VALUES (NEWID(), '{eventId}', '{playerId}', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            """);
        return (eventId, playerId);
    }

    private async Task<bool> TableExistsAsync(string table) =>
        await ScalarAsync<int>($"SELECT COUNT(*) FROM sys.tables WHERE [name] = N'{table}'") == 1;

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
