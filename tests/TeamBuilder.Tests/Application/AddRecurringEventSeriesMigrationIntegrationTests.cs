using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves the AddRecurringEventSeries migration on a real SQL Server database: an existing
/// series row gets MaxParticipants 50, the range CHECK and the filtered unique occurrence index
/// exist and behave, Down removes exactly those additions, and the model has no pending changes.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class AddRecurringEventSeriesMigrationIntegrationTests : IAsyncLifetime
{
    private const string CheckName = "CK_EventSeries_MaxParticipants_Range";
    private const string IndexName = "UX_Events_SeriesId_ScheduledStartUtc";

    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public AddRecurringEventSeriesMigrationIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "recurringmig");
        await _db.MigrateToAsync(MigrationIds.AddEventOccurrenceSchedulingFoundation);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Up_AddsMaxParticipantsWithDefault50_AndTheRangeCheck()
    {
        var seriesId = await SeedSeriesAsync();

        await _db.MigrateToAsync(MigrationIds.AddRecurringEventSeries);

        (await ScalarAsync<int>($"SELECT [MaxParticipants] FROM [EventSeries] WHERE [Id] = '{seriesId}'")).Should().Be(50);
        (await ScalarAsync<bool>("SELECT [is_nullable] FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[EventSeries]') AND [name] = N'MaxParticipants'"))
            .Should().BeFalse();
        (await ScalarAsync<string>($"SELECT [definition] FROM sys.check_constraints WHERE [name] = N'{CheckName}'"))
            .Should().Be("([MaxParticipants]>=(1) AND [MaxParticipants]<=(100000))");

        foreach (var value in new[] { 0, -1, 100001 })
        {
            var act = () => ExecuteAsync($"UPDATE [EventSeries] SET [MaxParticipants] = {value} WHERE [Id] = '{seriesId}'");
            (await act.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain(CheckName);
        }

        await ExecuteAsync($"UPDATE [EventSeries] SET [MaxParticipants] = 100000 WHERE [Id] = '{seriesId}'");
        await ExecuteAsync($"UPDATE [EventSeries] SET [MaxParticipants] = 1 WHERE [Id] = '{seriesId}'");
    }

    [Fact]
    public async Task Up_CreatesTheFilteredUniqueOccurrenceIndex()
    {
        await _db.MigrateToAsync(MigrationIds.AddRecurringEventSeries);

        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT i.[is_unique], i.[has_filter], i.[filter_definition],
                   STRING_AGG(c.[name], ',') WITHIN GROUP (ORDER BY ic.[key_ordinal])
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
            JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
            WHERE i.[object_id] = OBJECT_ID(N'[Events]') AND i.[name] = N'{IndexName}'
            GROUP BY i.[is_unique], i.[has_filter], i.[filter_definition];
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetBoolean(0).Should().BeTrue();
        reader.GetBoolean(1).Should().BeTrue();
        reader.GetString(2).Should().Be("([SeriesId] IS NOT NULL)");
        reader.GetString(3).Should().Be("SeriesId,ScheduledStartUtc");
    }

    [Fact]
    public async Task Up_DuplicateSeriesStartIsRejected_OneOffsAreNot()
    {
        var seriesId = await SeedSeriesAsync();
        await _db.MigrateToAsync(MigrationIds.AddRecurringEventSeries);

        await InsertOccurrenceAsync(seriesId);
        var act = () => InsertOccurrenceAsync(seriesId);
        (await act.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain(IndexName);

        await InsertOccurrenceAsync(null);
        await InsertOccurrenceAsync(null);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM [Events] WHERE [SeriesId] IS NULL")).Should().Be(2);
    }

    [Fact]
    public async Task Down_RemovesTheColumnCheckAndIndex_AndKeepsTheRest()
    {
        var seriesId = await SeedSeriesAsync();
        await _db.MigrateToAsync(MigrationIds.AddRecurringEventSeries);
        await InsertOccurrenceAsync(seriesId);

        await _db.MigrateToAsync(MigrationIds.AddEventOccurrenceSchedulingFoundation);

        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[EventSeries]') AND [name] = N'MaxParticipants'")).Should().Be(0);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM sys.check_constraints WHERE [name] = N'{CheckName}'")).Should().Be(0);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM sys.indexes WHERE [name] = N'{IndexName}'")).Should().Be(0);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.check_constraints WHERE [name] = N'CK_EventSeries_DurationMinutes_Range'")).Should().Be(1);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.indexes WHERE [name] = N'IX_Events_SeriesId'")).Should().Be(1);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [EventSeries] WHERE [Id] = '{seriesId}'")).Should().Be(1);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [Events] WHERE [SeriesId] = '{seriesId}'")).Should().Be(1);

        // Without the index, duplicates are possible again (proves it was the index that refused them).
        await InsertOccurrenceAsync(seriesId);

        // Forward again is clean on an empty series table.
        await ExecuteAsync("DELETE FROM [Events]; DELETE FROM [EventSeries];");
        await _db.MigrateToAsync(MigrationIds.AddRecurringEventSeries);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM sys.indexes WHERE [name] = N'{IndexName}'")).Should().Be(1);
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

    private static readonly DateTime Start = new(2026, 10, 13, 21, 0, 0, DateTimeKind.Utc);

    /// <summary>Inserts a series row on the pre-migration schema (no MaxParticipants column).</summary>
    private async Task<Guid> SeedSeriesAsync()
    {
        var id = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO [EventSeries] ([Id], [Name], [LocalStartTime], [DurationMinutes], [TimeZoneId], [RecurrenceRule], [SeriesStartDate], [Status], [CreatedAtUtc])
            VALUES ('{id}', N'Legacy series', '17:00', 90, N'America/New_York', N'FREQ=WEEKLY;BYDAY=TU', '2026-10-13', 1, SYSUTCDATETIME());
            """);
        return id;
    }

    private async Task InsertOccurrenceAsync(Guid? seriesId)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO [Events] ([Id], [Name], [ScheduledStartUtc], [Status], [MaxParticipants], [CurrentParticipantCount], [IsDetached], [SeriesId], [CreatedAtUtc])
            VALUES (NEWID(), N'Occurrence', @start, 1, 10, 0, 0, @series, SYSUTCDATETIME());
            """;
        command.Parameters.Add(new SqlParameter("@start", System.Data.SqlDbType.DateTime2) { Value = Start });
        command.Parameters.Add(new SqlParameter("@series", System.Data.SqlDbType.UniqueIdentifier) { Value = (object?)seriesId ?? DBNull.Value });
        await command.ExecuteNonQueryAsync();
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
