using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves the AddEventSeriesMaterializationCheckpoint migration on a real SQL Server database:
/// existing series gain a NULL ("unknown") checkpoint with their occurrences untouched, Down
/// removes only the column, and the fully migrated model has no pending changes.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class AddEventSeriesMaterializationCheckpointMigrationIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public AddEventSeriesMaterializationCheckpointMigrationIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "checkpointmig");
        await _db.MigrateToAsync(MigrationIds.AddRecurringEventSeries);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Up_AddsANullableDateColumn_AndLeavesExistingSeriesUnknown()
    {
        var seriesId = await SeedSeriesWithOccurrenceAsync();

        await _db.MigrateToAsync(MigrationIds.AddEventSeriesMaterializationCheckpoint);

        (await ScalarAsync<string>("SELECT TYPE_NAME([system_type_id]) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[EventSeries]') AND [name] = N'MaterializedThroughLocalDate'"))
            .Should().Be("date");
        (await ScalarAsync<bool>("SELECT [is_nullable] FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[EventSeries]') AND [name] = N'MaterializedThroughLocalDate'"))
            .Should().BeTrue();
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [EventSeries] WHERE [Id] = '{seriesId}' AND [MaterializedThroughLocalDate] IS NULL")).Should().Be(1);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [Events] WHERE [SeriesId] = '{seriesId}'")).Should().Be(1);

        await ExecuteAsync($"UPDATE [EventSeries] SET [MaterializedThroughLocalDate] = '2026-11-02' WHERE [Id] = '{seriesId}'");
    }

    [Fact]
    public async Task Down_RemovesOnlyTheColumn()
    {
        var seriesId = await SeedSeriesWithOccurrenceAsync();
        await _db.MigrateToAsync(MigrationIds.AddEventSeriesMaterializationCheckpoint);

        await _db.MigrateToAsync(MigrationIds.AddRecurringEventSeries);

        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[EventSeries]') AND [name] = N'MaterializedThroughLocalDate'")).Should().Be(0);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [EventSeries] WHERE [Id] = '{seriesId}'")).Should().Be(1);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [Events] WHERE [SeriesId] = '{seriesId}'")).Should().Be(1);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.indexes WHERE [name] = N'UX_Events_SeriesId_ScheduledStartUtc'")).Should().Be(1);
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

    /// <summary>A series and one generated occurrence on the pre-migration schema.</summary>
    private async Task<Guid> SeedSeriesWithOccurrenceAsync()
    {
        var id = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO [EventSeries] ([Id], [Name], [LocalStartTime], [DurationMinutes], [TimeZoneId], [RecurrenceRule], [SeriesStartDate], [Status], [MaxParticipants], [CreatedAtUtc])
            VALUES ('{id}', N'Existing series', '17:00', 90, N'America/New_York', N'FREQ=WEEKLY;BYDAY=TU', '2026-10-13', 1, 12, SYSUTCDATETIME());
            INSERT INTO [Events] ([Id], [Name], [ScheduledStartUtc], [Status], [MaxParticipants], [CurrentParticipantCount], [IsDetached], [SeriesId], [OccurrenceIndex], [CreatedAtUtc])
            VALUES (NEWID(), N'Existing series', '2026-10-13T21:00:00', 1, 12, 0, 0, '{id}', 0, SYSUTCDATETIME());
            """);
        return id;
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
