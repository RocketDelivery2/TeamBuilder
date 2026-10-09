using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Infrastructure.Data.Configurations;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves the AddVacancyOutboxSubscriptionsAndNotifications migration on a real SQL Server 2022
/// database: on an empty database (every table, index, filter, include, uniqueness and FK
/// action as designed, no pending model changes), on a database at the previous (#177)
/// migration holding a live game whose rows all survive, and Down then Up again.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class AddVacancyOutboxSubscriptionsAndNotificationsMigrationIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public AddVacancyOutboxSubscriptionsAndNotificationsMigrationIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "refillmig");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task EmptyDatabase_GetsTheRefillSchema_WithNoPendingModelChanges()
    {
        await _db.MigrateToAsync();

        // Outbox: dedup uniqueness and the two filtered claim indexes.
        (await IndexAsync(OutboxMessageConfiguration.TableName, OutboxMessageConfiguration.DeduplicationKeyUniqueIndexName))
            .Should().Be((true, null, "DeduplicationKey", ""));
        (await IndexAsync(OutboxMessageConfiguration.TableName, OutboxMessageConfiguration.PendingIndexName))
            .Should().Be((false, "([Status]=(1))", "NextAttemptAtUtc,CreatedAtUtc", "AttemptCount"));
        (await IndexAsync(OutboxMessageConfiguration.TableName, OutboxMessageConfiguration.ProcessingIndexName))
            .Should().Be((false, "([Status]=(2))", "LockExpiresAtUtc", "AttemptCount"));
        foreach (var check in new[] { OutboxMessageConfiguration.StatusRangeCheckName, OutboxMessageConfiguration.AttemptCountCheckName })
            (await ScalarAsync<int>($"SELECT COUNT(*) FROM sys.check_constraints WHERE [name] = N'{check}'")).Should().Be(1, check);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.foreign_keys WHERE [parent_object_id] = OBJECT_ID(N'OutboxMessages')")).Should().Be(0, "a message outlives what it refers to");

        // Subscriptions: one per (player, occurrence, requirement); fan-out lookup by requirement.
        (await IndexAsync(OccurrenceRosterSubscriptionConfiguration.TableName, OccurrenceRosterSubscriptionConfiguration.PlayerOccurrenceRequirementUniqueIndexName))
            .Should().Be((true, null, "PlayerId,OccurrenceId,RosterRequirementId", ""));
        (await IndexAsync(OccurrenceRosterSubscriptionConfiguration.TableName, OccurrenceRosterSubscriptionConfiguration.RequirementOccurrenceIndexName))
            .Should().Be((false, null, "RosterRequirementId,OccurrenceId", "PlayerId"));
        (await ForeignKeyActionAsync(OccurrenceRosterSubscriptionConfiguration.RequirementForeignKeyName)).Should().Be("CASCADE");
        (await ForeignKeyActionAsync(OccurrenceRosterSubscriptionConfiguration.PlayerForeignKeyName)).Should().Be("CASCADE");

        // Notifications: consumer idempotency and the caller's timeline (all / unread).
        (await IndexAsync(InAppNotificationConfiguration.TableName, InAppNotificationConfiguration.SourceEventPlayerUniqueIndexName))
            .Should().Be((true, null, "SourceEventId,PlayerId", ""));
        (await IndexAsync(InAppNotificationConfiguration.TableName, InAppNotificationConfiguration.PlayerTimelineIndexName))
            .Should().Be((false, null, "PlayerId,CreatedAtUtc DESC,Id DESC", "ReadAtUtc"));
        (await IndexAsync(InAppNotificationConfiguration.TableName, InAppNotificationConfiguration.PlayerUnreadIndexName))
            .Should().Be((false, "([ReadAtUtc] IS NULL)", "PlayerId,CreatedAtUtc DESC,Id DESC", ""));
        (await ForeignKeyActionAsync(InAppNotificationConfiguration.PlayerForeignKeyName)).Should().Be("CASCADE");
        (await ForeignKeyActionAsync(InAppNotificationConfiguration.OccurrenceForeignKeyName)).Should().Be("NO_ACTION");

        await using var context = _db.CreateContext();
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public async Task ExistingGameAtThePreviousMigration_SurvivesUp_AndCanBeSubscribedTo()
    {
        await _db.MigrateToAsync(MigrationIds.AddVenueOwnershipPrivacyAndSearchLocation);
        var (playerId, eventId, requirementId, assignmentId) = await SeedGameAsync();

        await _db.MigrateToAsync(MigrationIds.AddVacancyOutboxSubscriptionsAndNotifications);

        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [Events] WHERE [Id] = '{eventId}'")).Should().Be(1);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [RosterRequirements] WHERE [Id] = '{requirementId}' AND [RequiredCount] = 10")).Should().Be(1);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [RosterAssignments] WHERE [Id] = '{assignmentId}' AND [Status] = 2")).Should().Be(1);
        foreach (var table in new[] { "OutboxMessages", "OccurrenceRosterSubscriptions", "InAppNotifications" })
            (await ScalarAsync<int>($"SELECT COUNT(*) FROM [{table}]")).Should().Be(0, $"nothing is backfilled into {table}");

        // The composite FK refuses a requirement of another occurrence.
        var foreignOccurrence = await SeedEventAsync();
        var act = () => ExecuteAsync($"""
            INSERT INTO [OccurrenceRosterSubscriptions] ([Id], [PlayerId], [OccurrenceId], [RosterRequirementId], [CreatedAtUtc])
            VALUES (NEWID(), '{playerId}', '{foreignOccurrence}', '{requirementId}', SYSUTCDATETIME());
            """);
        (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(547);

        await ExecuteAsync($"""
            INSERT INTO [OccurrenceRosterSubscriptions] ([Id], [PlayerId], [OccurrenceId], [RosterRequirementId], [CreatedAtUtc])
            VALUES (NEWID(), '{playerId}', '{eventId}', '{requirementId}', SYSUTCDATETIME());
            """);
        var duplicate = () => ExecuteAsync($"""
            INSERT INTO [OccurrenceRosterSubscriptions] ([Id], [PlayerId], [OccurrenceId], [RosterRequirementId], [CreatedAtUtc])
            VALUES (NEWID(), '{playerId}', '{eventId}', '{requirementId}', SYSUTCDATETIME());
            """);
        (await duplicate.Should().ThrowAsync<SqlException>()).Which.Number.Should().BeOneOf(2601, 2627);
    }

    [Fact]
    public async Task DeletingAnOccurrenceWithoutHistory_CascadesItsSubscriptions()
    {
        await _db.MigrateToAsync();
        var playerId = await SeedPlayerAsync();
        var eventId = await SeedEventAsync();
        var requirementId = await SeedRequirementAsync(eventId);
        await ExecuteAsync($"""
            INSERT INTO [OccurrenceRosterSubscriptions] ([Id], [PlayerId], [OccurrenceId], [RosterRequirementId], [CreatedAtUtc])
            VALUES (NEWID(), '{playerId}', '{eventId}', '{requirementId}', SYSUTCDATETIME());
            """);

        await ExecuteAsync($"DELETE FROM [Events] WHERE [Id] = '{eventId}';");

        (await ScalarAsync<int>("SELECT COUNT(*) FROM [OccurrenceRosterSubscriptions]")).Should().Be(0);
    }

    [Fact]
    public async Task Down_DropsTheThreeTables_KeepsTheRoster_AndUpWorksAgain()
    {
        await _db.MigrateToAsync(MigrationIds.AddVenueOwnershipPrivacyAndSearchLocation);
        var (_, eventId, _, assignmentId) = await SeedGameAsync();
        await _db.MigrateToAsync(MigrationIds.AddVacancyOutboxSubscriptionsAndNotifications);

        await _db.MigrateToAsync(MigrationIds.AddVenueOwnershipPrivacyAndSearchLocation);

        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE [name] IN (N'OutboxMessages', N'OccurrenceRosterSubscriptions', N'InAppNotifications')")).Should().Be(0);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [RosterAssignments] WHERE [Id] = '{assignmentId}' AND [OccurrenceId] = '{eventId}'")).Should().Be(1);

        await _db.MigrateToAsync();
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE [name] IN (N'OutboxMessages', N'OccurrenceRosterSubscriptions', N'InAppNotifications')")).Should().Be(3);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<(Guid PlayerId, Guid EventId, Guid RequirementId, Guid AssignmentId)> SeedGameAsync()
    {
        var playerId = await SeedPlayerAsync();
        var eventId = await SeedEventAsync();
        var requirementId = await SeedRequirementAsync(eventId);
        var assignmentId = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO [RosterAssignments] ([Id], [OccurrenceId], [PlayerId], [RequirementId], [RoleCode], [Status], [Source], [ConfirmedAtUtc], [CreatedAtUtc])
            VALUES ('{assignmentId}', '{eventId}', '{playerId}', '{requirementId}', N'participant', 2, 2, SYSUTCDATETIME(), SYSUTCDATETIME());
            """);
        return (playerId, eventId, requirementId, assignmentId);
    }

    private async Task<Guid> SeedPlayerAsync()
    {
        var id = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO [Players] ([Id], [Username], [CreatedAtUtc]) VALUES ('{id}', N'p{id:N}', SYSUTCDATETIME());");
        return id;
    }

    private async Task<Guid> SeedEventAsync()
    {
        var id = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO [Events] ([Id], [Name], [ScheduledStartUtc], [Status], [MaxParticipants], [CurrentParticipantCount], [IsDetached], [Category], [CreatedAtUtc])
            VALUES ('{id}', N'Wednesday Basketball', '2026-10-15T01:00:00', 2, 10, 0, 0, N'basketball', SYSUTCDATETIME());
            """);
        return id;
    }

    private async Task<Guid> SeedRequirementAsync(Guid eventId)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO [RosterRequirements] ([Id], [OccurrenceId], [RoleCode], [RequiredCount], [CreatedAtUtc])
            VALUES ('{id}', '{eventId}', N'participant', 10, SYSUTCDATETIME());
            """);
        return id;
    }

    /// <summary>(is unique, filter, key columns with DESC marks, included columns), comma-joined in key order.</summary>
    private async Task<(bool Unique, string? Filter, string Keys, string Includes)> IndexAsync(string table, string index)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT i.[is_unique], i.[filter_definition], c.[name], ic.[is_descending_key], ic.[is_included_column]
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
            JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
            WHERE i.[object_id] = OBJECT_ID(N'{table}') AND i.[name] = N'{index}'
            ORDER BY ic.[is_included_column], ic.[key_ordinal], c.[name];
            """;
        await using var reader = await command.ExecuteReaderAsync();
        bool? unique = null;
        string? filter = null;
        var keys = new List<string>();
        var includes = new List<string>();
        while (await reader.ReadAsync())
        {
            unique = reader.GetBoolean(0);
            filter = reader.IsDBNull(1) ? null : reader.GetString(1);
            var name = reader.GetString(2);
            if (reader.GetBoolean(4))
                includes.Add(name);
            else
                keys.Add(reader.GetBoolean(3) ? $"{name} DESC" : name);
        }

        unique.Should().NotBeNull($"index {index} must exist");
        return (unique!.Value, filter, string.Join(",", keys), string.Join(",", includes));
    }

    private Task<string?> ForeignKeyActionAsync(string name) =>
        ScalarAsync<string>($"SELECT [delete_referential_action_desc] FROM sys.foreign_keys WHERE [name] = N'{name}'");

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
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }
}
