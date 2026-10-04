using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves the AddEventOccurrenceSchedulingFoundation migration against a real SQL Server
/// database seeded (via raw SQL) on the exact pre-migration Events schema: rows are renamed in
/// place (same table, same ids, same values, same roster links), the new columns are empty, the
/// Venues/EventSeries tables, FKs and indexes exist with the intended delete behavior, Down
/// restores the legacy columns with their data, and the model has no pending changes.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class AddEventOccurrenceSchedulingFoundationMigrationIntegrationTests : IAsyncLifetime
{
    // datetime2(7) value with full sub-second precision, to prove the value moves untouched.
    private static readonly DateTime LegacyStart = new DateTime(2026, 11, 5, 18, 30, 15, DateTimeKind.Utc).AddTicks(1234567);
    private const string LegacyLocationText = "  Riverside Park — Field #3 (north gate)  ";

    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public AddEventOccurrenceSchedulingFoundationMigrationIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "occurrencemig");
        await _db.MigrateToAsync(MigrationIds.SeparateTeamLifecycleFromRecruitment);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Up_RenamesEventsInPlace_PreservingIdsValuesAndRosterLinks()
    {
        var seed = await SeedLegacyAsync();

        await _db.MigrateToAsync(MigrationIds.AddEventOccurrenceSchedulingFoundation);

        await using var context = _db.CreateContext();
        var occurrences = await context.Events.AsNoTracking()
            .Where(e => e.Id == seed.TeamEventId || e.Id == seed.PickupEventId)
            .ToDictionaryAsync(e => e.Id);

        occurrences.Should().HaveCount(2);
        var teamEvent = occurrences[seed.TeamEventId];
        teamEvent.ScheduledStartUtc.Should().Be(LegacyStart);
        teamEvent.ScheduledStartUtc.Ticks.Should().Be(LegacyStart.Ticks);
        teamEvent.LegacyLocation.Should().Be(LegacyLocationText);
        teamEvent.TeamId.Should().Be(seed.TeamId);
        teamEvent.HostId.Should().Be(seed.HostId);
        teamEvent.Name.Should().Be("Legacy team event");

        var pickup = occurrences[seed.PickupEventId];
        pickup.TeamId.Should().BeNull();
        pickup.LegacyLocation.Should().BeNull();

        foreach (var occurrence in occurrences.Values)
        {
            occurrence.SeriesId.Should().BeNull();
            occurrence.VenueId.Should().BeNull();
            occurrence.ScheduledEndUtc.Should().BeNull();
            occurrence.IsDetached.Should().BeFalse();
            occurrence.OccurrenceIndex.Should().BeNull();
        }

        var roster = await context.RosterEntries.AsNoTracking().SingleAsync(r => r.Id == seed.RosterEntryId);
        roster.EventId.Should().Be(seed.TeamEventId);
        (await context.RosterEntries.AsNoTracking()
            .Include(r => r.Event)
            .SingleAsync(r => r.Id == seed.RosterEntryId)).Event.Id.Should().Be(seed.TeamEventId);
    }

    [Fact]
    public async Task Up_KeepsTheEventsTable_AndCreatesNoSecondOccurrenceTable()
    {
        await _db.MigrateToAsync(MigrationIds.AddEventOccurrenceSchedulingFoundation);

        var tables = await UserTablesAsync();
        tables.Should().Contain(["Events", "Venues", "EventSeries"]);
        tables.Should().NotContain(t => t.Contains("Occurrence", StringComparison.OrdinalIgnoreCase));
        tables.Where(t => t.Contains("Event", StringComparison.OrdinalIgnoreCase))
            .Should().BeEquivalentTo("Events", "EventSeries");

        (await ColumnsAsync("Events")).Should().Contain(
                ["ScheduledStartUtc", "LegacyLocation", "ScheduledEndUtc", "SeriesId", "VenueId", "IsDetached", "OccurrenceIndex"])
            .And.NotContain(["EventDateUtc", "Location"]);
        (await IsNullableAsync("Events", "ScheduledStartUtc")).Should().BeFalse();
        (await IsNullableAsync("Events", "ScheduledEndUtc")).Should().BeTrue();
        (await IsNullableAsync("Events", "IsDetached")).Should().BeFalse();

        (await ColumnsAsync("Venues")).Should().Contain(
            ["Id", "Name", "AddressLine1", "AddressLine2", "City", "StateOrProvince", "PostalCode", "CountryCode",
             "Latitude", "Longitude", "TimeZoneId", "VenueType", "CreatedAtUtc", "UpdatedAtUtc", "RowVersion"]);
        (await IsNullableAsync("Venues", "Latitude")).Should().BeTrue();
        (await IsNullableAsync("Venues", "Longitude")).Should().BeTrue();
        (await ScalarAsync<string>(
            "SELECT CONCAT(TYPE_NAME(system_type_id), '(', [precision], ',', [scale], ')') FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[Venues]') AND [name] = N'Latitude'"))
            .Should().Be("decimal(9,6)");

        (await ColumnsAsync("EventSeries")).Should().Contain(
            ["Id", "Name", "Description", "Category", "Tags", "TeamId", "HostId", "VenueId", "LocalStartTime",
             "DurationMinutes", "TimeZoneId", "RecurrenceRule", "SeriesStartDate", "SeriesEndDate", "Status",
             "CreatedAtUtc", "UpdatedAtUtc", "RowVersion"]);

        (await CheckConstraintsAsync("Venues")).Should().Contain(["CK_Venues_Latitude_Range", "CK_Venues_Longitude_Range"]);
        (await CheckConstraintsAsync("EventSeries")).Should().Contain("CK_EventSeries_DurationMinutes_Range");
    }

    [Fact]
    public async Task Up_CreatesForeignKeysWithTheIntendedDeleteBehavior()
    {
        await _db.MigrateToAsync(MigrationIds.AddEventOccurrenceSchedulingFoundation);

        var fks = await ForeignKeysAsync();

        fks[("Events", "HostId")].Should().Be(("Players", "SET_NULL"));
        fks[("Events", "TeamId")].Should().Be(("Teams", "SET_NULL"));
        fks[("Events", "SeriesId")].Should().Be(("EventSeries", "SET_NULL"));
        fks[("Events", "VenueId")].Should().Be(("Venues", "NO_ACTION"));
        fks[("EventSeries", "HostId")].Should().Be(("Players", "SET_NULL"));
        fks[("EventSeries", "TeamId")].Should().Be(("Teams", "SET_NULL"));
        fks[("EventSeries", "VenueId")].Should().Be(("Venues", "NO_ACTION"));

        // Unchanged: roster entries still belong to the Events table.
        fks[("RosterEntries", "EventId")].Should().Be(("Events", "CASCADE"));
    }

    [Fact]
    public async Task Up_CreatesTheFoundationIndexes_AndNoSeriesStartUniqueness()
    {
        await _db.MigrateToAsync(MigrationIds.AddEventOccurrenceSchedulingFoundation);

        var events = await IndexesAsync("Events");
        events["IX_Events_ScheduledStartUtc"].Should().Equal("ScheduledStartUtc");
        events["IX_Events_SeriesId"].Should().Equal("SeriesId");
        events["IX_Events_VenueId"].Should().Equal("VenueId");
        events["IX_Events_TeamId"].Should().Equal("TeamId");
        events["IX_Events_HostId"].Should().Equal("HostId");
        events["IX_Events_Status"].Should().Equal("Status");
        events["IX_Events_Category"].Should().Equal("Category");
        events.Keys.Should().NotContain("IX_Events_EventDateUtc");
        events.Keys.Should().NotContain(k => k.StartsWith("UX_", StringComparison.Ordinal));
        events.Values.Should().NotContain(cols => cols.SequenceEqual(new[] { "SeriesId", "ScheduledStartUtc" }));

        var series = await IndexesAsync("EventSeries");
        series["IX_EventSeries_TeamId"].Should().Equal("TeamId");
        series["IX_EventSeries_HostId"].Should().Equal("HostId");
        series["IX_EventSeries_VenueId"].Should().Equal("VenueId");
        series["IX_EventSeries_Status"].Should().Equal("Status");

        (await ScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE [type] = 4")).Should().Be(0);
    }

    [Fact]
    public async Task Down_RestoresLegacyColumnsAndData_AndDropsTheNewTables()
    {
        var seed = await SeedLegacyAsync();

        await _db.MigrateToAsync(MigrationIds.AddEventOccurrenceSchedulingFoundation);
        await _db.MigrateToAsync(MigrationIds.SeparateTeamLifecycleFromRecruitment);

        (await UserTablesAsync()).Should().NotContain(["Venues", "EventSeries"]);
        (await ColumnsAsync("Events")).Should().Contain(["EventDateUtc", "Location"])
            .And.NotContain(["ScheduledStartUtc", "LegacyLocation", "ScheduledEndUtc", "SeriesId", "VenueId", "IsDetached", "OccurrenceIndex"]);
        (await IndexesAsync("Events"))["IX_Events_EventDateUtc"].Should().Equal("EventDateUtc");

        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT [EventDateUtc], [Location] FROM [Events] WHERE [Id] = @id;";
        command.Parameters.AddWithValue("@id", seed.TeamEventId);
        await using (var reader = await command.ExecuteReaderAsync())
        {
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetDateTime(0).Ticks.Should().Be(LegacyStart.Ticks);
            reader.GetString(1).Should().Be(LegacyLocationText);
        }

        (await ScalarAsync<Guid>($"SELECT [EventId] FROM [RosterEntries] WHERE [Id] = '{seed.RosterEntryId}'"))
            .Should().Be(seed.TeamEventId);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM [Events]")).Should().Be(2);

        // And forward again: the rows come back unchanged.
        await _db.MigrateToAsync(MigrationIds.AddEventOccurrenceSchedulingFoundation);
        await using var context = _db.CreateContext();
        var occurrence = await context.Events.AsNoTracking().SingleAsync(e => e.Id == seed.TeamEventId);
        occurrence.ScheduledStartUtc.Ticks.Should().Be(LegacyStart.Ticks);
        occurrence.LegacyLocation.Should().Be(LegacyLocationText);
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

    private sealed record LegacySeed(Guid HostId, Guid TeamId, Guid TeamEventId, Guid PickupEventId, Guid RosterEntryId);

    /// <summary>Seeds a player, a team, a team event, a pickup event and a roster entry on the pre-migration schema.</summary>
    private async Task<LegacySeed> SeedLegacyAsync()
    {
        var hostId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var teamEventId = Guid.NewGuid();
        var pickupEventId = Guid.NewGuid();
        var rosterEntryId = Guid.NewGuid();

        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO [Players] ([Id], [Username], [CreatedAtUtc])
            VALUES (@host, @username, SYSUTCDATETIME());

            INSERT INTO [Teams] ([Id], [Name], [LifecycleStatus], [IsAcceptingMembers], [MaxMembers], [CurrentMemberCount], [OwnerId], [CreatedAtUtc])
            VALUES (@team, N'Legacy team', 1, 1, 10, 0, @host, SYSUTCDATETIME());

            INSERT INTO [Events] ([Id], [Name], [EventDateUtc], [Status], [Location], [MaxParticipants], [CurrentParticipantCount], [TeamId], [HostId], [CreatedAtUtc])
            VALUES (@teamEvent, N'Legacy team event', @start, 1, @location, 20, 1, @team, @host, SYSUTCDATETIME());

            INSERT INTO [Events] ([Id], [Name], [EventDateUtc], [Status], [Location], [MaxParticipants], [CurrentParticipantCount], [TeamId], [HostId], [CreatedAtUtc])
            VALUES (@pickupEvent, N'Legacy pickup', DATEADD(day, 1, @start), 2, NULL, 12, 0, NULL, @host, SYSUTCDATETIME());

            INSERT INTO [RosterEntries] ([Id], [EventId], [PlayerId], [IsConfirmed], [RegisteredAtUtc], [CreatedAtUtc])
            VALUES (@roster, @teamEvent, @host, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        command.Parameters.AddWithValue("@host", hostId);
        command.Parameters.AddWithValue("@username", $"host_{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("@team", teamId);
        command.Parameters.AddWithValue("@teamEvent", teamEventId);
        command.Parameters.AddWithValue("@pickupEvent", pickupEventId);
        command.Parameters.Add(new SqlParameter("@start", System.Data.SqlDbType.DateTime2) { Value = LegacyStart });
        command.Parameters.AddWithValue("@location", LegacyLocationText);
        command.Parameters.AddWithValue("@roster", rosterEntryId);
        await command.ExecuteNonQueryAsync();

        return new LegacySeed(hostId, teamId, teamEventId, pickupEventId, rosterEntryId);
    }

    private async Task<List<string>> UserTablesAsync() =>
        await StringListAsync("SELECT [name] FROM sys.tables WHERE [is_ms_shipped] = 0 AND [name] <> '__EFMigrationsHistory'");

    private async Task<List<string>> ColumnsAsync(string table) =>
        await StringListAsync($"SELECT [name] FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[{table}]')");

    private async Task<List<string>> CheckConstraintsAsync(string table) =>
        await StringListAsync($"SELECT [name] FROM sys.check_constraints WHERE [parent_object_id] = OBJECT_ID(N'[{table}]')");

    private async Task<bool> IsNullableAsync(string table, string column) =>
        await ScalarAsync<bool>(
            $"SELECT [is_nullable] FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[{table}]') AND [name] = N'{column}'");

    private async Task<Dictionary<(string Table, string Column), (string Referenced, string OnDelete)>> ForeignKeysAsync()
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT OBJECT_NAME(fk.[parent_object_id]), c.[name], OBJECT_NAME(fk.[referenced_object_id]), fk.[delete_referential_action_desc]
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.[constraint_object_id] = fk.[object_id]
            JOIN sys.columns c ON c.[object_id] = fkc.[parent_object_id] AND c.[column_id] = fkc.[parent_column_id];
            """;
        var result = new Dictionary<(string, string), (string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[(reader.GetString(0), reader.GetString(1))] = (reader.GetString(2), reader.GetString(3));
        return result;
    }

    private async Task<Dictionary<string, List<string>>> IndexesAsync(string table)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT i.[name], c.[name]
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
            JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
            WHERE i.[object_id] = OBJECT_ID(N'[{table}]') AND i.[is_primary_key] = 0 AND ic.[is_included_column] = 0
            ORDER BY i.[name], ic.[key_ordinal];
            """;
        var result = new Dictionary<string, List<string>>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            if (!result.TryGetValue(name, out var columns))
                result[name] = columns = [];
            columns.Add(reader.GetString(1));
        }
        return result;
    }

    private async Task<List<string>> StringListAsync(string sql)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values;
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
