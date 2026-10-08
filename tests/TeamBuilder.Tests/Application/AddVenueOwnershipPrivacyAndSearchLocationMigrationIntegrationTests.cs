using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data.Configurations;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves the AddVenueOwnershipPrivacyAndSearchLocation migration on a real SQL Server 2022
/// database: on an empty database, and on a database at the previous migration holding a
/// legacy venue with no coordinates, a physical venue, a virtual venue and their events. Every
/// row survives, nothing is fabricated (existing venues become Public with no creator), the
/// persisted computed geography exists only where both coordinates exist on a physical venue,
/// with latitude/longitude in the right order, the spatial index is created, and Down restores
/// the previous schema with the rows intact.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class AddVenueOwnershipPrivacyAndSearchLocationMigrationIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public AddVenueOwnershipPrivacyAndSearchLocationMigrationIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "venuegeomig");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task EmptyDatabase_MigratesToTheGeoSchema_WithNoPendingModelChanges()
    {
        await _db.MigrateToAsync();

        (await ScalarAsync<bool>("SELECT [is_persisted] FROM sys.computed_columns WHERE [object_id] = OBJECT_ID(N'Venues') AND [name] = N'SearchLocation'")).Should().BeTrue();
        (await ScalarAsync<string>("SELECT TYPE_NAME([user_type_id]) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'Venues') AND [name] = N'SearchLocation'")).Should().Be("geography");
        (await ScalarAsync<string>($"SELECT [type_desc] FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'Venues') AND [name] = N'{VenueConfiguration.SearchLocationIndexName}'")).Should().Be("SPATIAL");
        (await ScalarAsync<string>($"SELECT [tessellation_scheme] FROM sys.spatial_index_tessellations t JOIN sys.indexes i ON i.[object_id] = t.[object_id] AND i.[index_id] = t.[index_id] WHERE i.[name] = N'{VenueConfiguration.SearchLocationIndexName}'")).Should().Be("GEOGRAPHY_AUTO_GRID");
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM sys.indexes WHERE [name] = N'{EventOccurrenceConfiguration.VenueStatusStartIndexName}'")).Should().Be(1);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.indexes WHERE [name] = N'IX_Events_VenueId'")).Should().Be(0, "the composite index replaces it");
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.index_columns ic JOIN sys.indexes i ON i.[object_id] = ic.[object_id] AND i.[index_id] = ic.[index_id] JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id] WHERE i.[name] = N'IX_RosterAssignments_RequirementId_OccurrenceId' AND c.[name] = N'Status' AND ic.[is_included_column] = 1")).Should().Be(1);
        foreach (var check in new[] { VenueConfiguration.LatitudeRangeCheckName, VenueConfiguration.LongitudeRangeCheckName, VenueConfiguration.PrivacyLevelRangeCheckName })
            (await ScalarAsync<int>($"SELECT COUNT(*) FROM sys.check_constraints WHERE [name] = N'{check}'")).Should().Be(1, check);

        await using var context = _db.CreateContext();
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public async Task ExistingVenuesAndEvents_SurviveUp_WithoutFabricatedCoordinates()
    {
        await _db.MigrateToAsync(MigrationIds.PreserveRosterHistoryOnOccurrenceDelete);
        var legacy = await SeedVenueAsync("Legacy gym", null, null, VenueType.Indoor);
        var physical = await SeedVenueAsync("Union Park court", 41.884900m, -87.666100m, VenueType.Outdoor);
        var latOnly = await SeedVenueAsync("Half-known court", 41.900000m, null, VenueType.Outdoor);
        var virtualVenue = await SeedVenueAsync("Discord voice", null, null, VenueType.Virtual);
        var events = new Dictionary<Guid, Guid>();
        foreach (var venueId in new[] { legacy, physical, latOnly, virtualVenue })
            events[venueId] = await SeedEventAsync(venueId);
        var orphanEvent = await SeedEventAsync(null);

        await _db.MigrateToAsync(MigrationIds.AddVenueOwnershipPrivacyAndSearchLocation);

        (await ScalarAsync<int>("SELECT COUNT(*) FROM [Venues]")).Should().Be(4);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM [Venues] WHERE [PrivacyLevel] = 1 AND [CreatedByPlayerId] IS NULL")).Should().Be(4);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM [Venues] WHERE [SearchLocation] IS NOT NULL")).Should().Be(1, "only the physical venue with both coordinates is searchable");
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [Venues] WHERE [Id] = '{legacy}' AND [Latitude] IS NULL AND [Longitude] IS NULL")).Should().Be(1);
        (await ScalarAsync<double>($"SELECT [SearchLocation].Lat FROM [Venues] WHERE [Id] = '{physical}'")).Should().BeApproximately(41.8849, 1e-9);
        (await ScalarAsync<double>($"SELECT [SearchLocation].Long FROM [Venues] WHERE [Id] = '{physical}'")).Should().BeApproximately(-87.6661, 1e-9);
        (await ScalarAsync<int>($"SELECT [SearchLocation].STSrid FROM [Venues] WHERE [Id] = '{physical}'")).Should().Be(4326);
        foreach (var (venueId, eventId) in events)
            (await ScalarAsync<int>($"SELECT COUNT(*) FROM [Events] WHERE [Id] = '{eventId}' AND [VenueId] = '{venueId}'")).Should().Be(1);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [Events] WHERE [Id] = '{orphanEvent}' AND [VenueId] IS NULL")).Should().Be(1);

        // The EF model still reads and writes the table (the computed column is outside it).
        await using (var context = _db.CreateContext())
        {
            var venue = await context.Venues.SingleAsync(v => v.Id == physical);
            venue.PrivacyLevel.Should().Be(VenuePrivacyLevel.Public);
            venue.CreatedByPlayerId.Should().BeNull();
            context.Venues.Add(new Venue { Id = Guid.NewGuid(), Name = "New private court", Latitude = 41.123456m, Longitude = -87.654321m, VenueType = VenueType.Outdoor, PrivacyLevel = VenuePrivacyLevel.Private, TimeZoneId = "America/Chicago" });
            await context.SaveChangesAsync();
        }

        // A Private venue's search point is snapped to the 0.01 degree grid.
        (await ScalarAsync<double>("SELECT [SearchLocation].Lat FROM [Venues] WHERE [Name] = N'New private court'")).Should().BeApproximately(41.12, 1e-9);
        (await ScalarAsync<double>("SELECT [SearchLocation].Long FROM [Venues] WHERE [Name] = N'New private court'")).Should().BeApproximately(-87.65, 1e-9);
    }

    [Fact]
    public async Task SearchLocationDefinition_MatchesTheConfigurationConstant()
    {
        await _db.MigrateToAsync();

        // The migration carries its SQL literally; SQL Server normalizes both definitions the
        // same way, so a drift between the migration and VenueConfiguration shows up here.
        var migrated = await ScalarAsync<string>("SELECT [definition] FROM sys.computed_columns WHERE [object_id] = OBJECT_ID(N'Venues') AND [name] = N'SearchLocation'");
        await ExecuteAsync($"""
            CREATE TABLE [SearchLocationProbe] ([Latitude] decimal(9,6) NULL, [Longitude] decimal(9,6) NULL, [VenueType] int NOT NULL, [PrivacyLevel] int NOT NULL,
                [SearchLocation] AS ({VenueConfiguration.SearchLocationColumnSql}) PERSISTED);
            """);
        var expected = await ScalarAsync<string>("SELECT [definition] FROM sys.computed_columns WHERE [object_id] = OBJECT_ID(N'SearchLocationProbe') AND [name] = N'SearchLocation'");
        await ExecuteAsync("DROP TABLE [SearchLocationProbe];");

        migrated.Should().Be(expected);
    }

    [Fact]
    public async Task DatabaseRangeChecks_RefuseImpossibleCoordinatesAndPrivacy()
    {
        await _db.MigrateToAsync();

        // A physical row: the computed column's range guard leaves an impossible coordinate
        // to the CHECK constraints (547) rather than failing inside geography::Point (6522).
        foreach (var (lat, lon) in new[] { ("90.000001", "0"), ("-90.5", "0"), ("0", "180.000001"), ("0", "-181") })
        {
            var act = () => InsertVenueAsync(lat, lon, venueType: 1, privacy: 1);
            (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(547, $"{lat},{lon}");
        }

        // A virtual row has no geography, so only the CHECK constraints stand in the way.
        foreach (var (lat, lon, privacy) in new[] { ("90.000001", "0", "1"), ("0", "-181", "1"), ("0", "0", "3"), ("0", "0", "0") })
        {
            var act = () => InsertVenueAsync(lat, lon, venueType: 3, privacy: int.Parse(privacy));
            (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(547, $"{lat},{lon},{privacy}");
        }
    }

    private Task InsertVenueAsync(string lat, string lon, int venueType, int privacy) =>
        ExecuteAsync($"""
            INSERT INTO [Venues] ([Id], [Name], [Latitude], [Longitude], [VenueType], [PrivacyLevel], [CreatedAtUtc])
            VALUES (NEWID(), N'bad', {lat}, {lon}, {venueType}, {privacy}, SYSUTCDATETIME());
            """);

    [Fact]
    public async Task Down_RestoresThePreviousSchema_AndKeepsTheRows()
    {
        await _db.MigrateToAsync(MigrationIds.PreserveRosterHistoryOnOccurrenceDelete);
        var physical = await SeedVenueAsync("Union Park court", 41.884900m, -87.666100m, VenueType.Outdoor);
        var eventId = await SeedEventAsync(physical);
        await _db.MigrateToAsync(MigrationIds.AddVenueOwnershipPrivacyAndSearchLocation);

        await _db.MigrateToAsync(MigrationIds.PreserveRosterHistoryOnOccurrenceDelete);

        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'Venues') AND [name] IN (N'SearchLocation', N'PrivacyLevel', N'CreatedByPlayerId')")).Should().Be(0);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.indexes WHERE [name] = N'IX_Events_VenueId'")).Should().Be(1);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM sys.indexes WHERE [name] IN (N'{EventOccurrenceConfiguration.VenueStatusStartIndexName}', N'{VenueConfiguration.SearchLocationIndexName}')")).Should().Be(0);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [Venues] WHERE [Id] = '{physical}' AND [Latitude] = 41.884900 AND [Longitude] = -87.666100")).Should().Be(1);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [Events] WHERE [Id] = '{eventId}' AND [VenueId] = '{physical}'")).Should().Be(1);

        // And back up again.
        await _db.MigrateToAsync();
        (await ScalarAsync<int>("SELECT COUNT(*) FROM [Venues] WHERE [SearchLocation] IS NOT NULL")).Should().Be(1);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<Guid> SeedVenueAsync(string name, decimal? latitude, decimal? longitude, VenueType type)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO [Venues] ([Id], [Name], [Latitude], [Longitude], [VenueType], [CreatedAtUtc])
            VALUES ('{id}', N'{name}', {Sql(latitude)}, {Sql(longitude)}, {(int)type}, SYSUTCDATETIME());
            """);
        return id;
    }

    private async Task<Guid> SeedEventAsync(Guid? venueId)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO [Events] ([Id], [Name], [ScheduledStartUtc], [Status], [MaxParticipants], [CurrentParticipantCount], [IsDetached], [VenueId], [Category], [CreatedAtUtc])
            VALUES ('{id}', N'Pickup', '2026-10-21T01:00:00', 2, 10, 0, 0, {(venueId is { } v ? $"'{v}'" : "NULL")}, N'basketball', SYSUTCDATETIME());
            """);
        return id;
    }

    private static string Sql(decimal? value) =>
        value is { } v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL";

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
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
    }
}
