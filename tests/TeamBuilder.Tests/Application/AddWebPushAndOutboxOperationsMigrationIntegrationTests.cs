using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Infrastructure.Data.Configurations;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves the AddWebPushAndOutboxOperations migration on a real SQL Server 2022 database: on an
/// empty database (both push tables, every index, filter, include, uniqueness, check and FK
/// action as designed, no pending model changes), on a database at the previous (#178)
/// migration holding outbox history and a notification that all survive with zeroed replay
/// counters, and Down then Up again.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class AddWebPushAndOutboxOperationsMigrationIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public AddWebPushAndOutboxOperationsMigrationIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "pushmig");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task EmptyDatabase_GetsThePushSchema_WithNoPendingModelChanges()
    {
        await _db.MigrateToAsync();

        // Subscriptions: one row per endpoint (by hash), a player's active devices by recency.
        (await IndexAsync(PushSubscriptionConfiguration.TableName, PushSubscriptionConfiguration.EndpointHashUniqueIndexName))
            .Should().Be((true, null, "EndpointHash", ""));
        (await IndexAsync(PushSubscriptionConfiguration.TableName, PushSubscriptionConfiguration.PlayerActiveIndexName))
            .Should().Be((false, "([IsActive]=(1))", "PlayerId,LastSeenAtUtc", ""));
        (await ForeignKeyActionAsync(PushSubscriptionConfiguration.PlayerForeignKeyName)).Should().Be("CASCADE");

        // Delivery ledger: one row per (notification, device), claim indexes per status, purge index.
        (await IndexAsync(PushDeliveryConfiguration.TableName, PushDeliveryConfiguration.NotificationSubscriptionUniqueIndexName))
            .Should().Be((true, null, "InAppNotificationId,PushSubscriptionId", ""));
        (await IndexAsync(PushDeliveryConfiguration.TableName, PushDeliveryConfiguration.PendingIndexName))
            .Should().Be((false, "([Status]=(1))", "NextAttemptAtUtc,CreatedAtUtc", "AttemptCount"));
        (await IndexAsync(PushDeliveryConfiguration.TableName, PushDeliveryConfiguration.SendingIndexName))
            .Should().Be((false, "([Status]=(2))", "LockExpiresAtUtc", "AttemptCount"));
        (await IndexAsync(PushDeliveryConfiguration.TableName, PushDeliveryConfiguration.CompletedIndexName))
            .Should().Be((false, "([Status]>=(3))", "CompletedAtUtc", ""));
        (await ForeignKeyActionAsync(PushDeliveryConfiguration.NotificationForeignKeyName)).Should().Be("CASCADE");
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.foreign_keys WHERE [parent_object_id] = OBJECT_ID(N'PushDeliveries')"))
            .Should().Be(1, "a delivery has no FK to its device (avoids multiple cascade paths; the dispatcher checks it)");

        // Outbox retention and replay inspection.
        (await IndexAsync(OutboxMessageConfiguration.TableName, OutboxMessageConfiguration.CompletedIndexName))
            .Should().Be((false, "([Status]=(3))", "ProcessedAtUtc", ""));
        (await IndexAsync(OutboxMessageConfiguration.TableName, OutboxMessageConfiguration.FailedIndexName))
            .Should().Be((false, "([Status]=(4))", "ProcessedAtUtc", "AggregateId,AttemptCount,ReplayCount,Type"));
        foreach (var check in new[] { OutboxMessageConfiguration.ReplayCountCheckName, PushDeliveryConfiguration.StatusRangeCheckName, PushSubscriptionConfiguration.FailureCountCheckName })
            (await ScalarAsync<int>($"SELECT COUNT(*) FROM sys.check_constraints WHERE [name] = N'{check}'")).Should().Be(1, check);

        await using var context = _db.CreateContext();
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public async Task ExistingOutboxHistory_SurvivesUp_WithZeroReplayCounters_AndDownThenUpWorks()
    {
        await _db.MigrateToAsync(MigrationIds.AddVacancyOutboxSubscriptionsAndNotifications);
        var playerId = await SeedPlayerAsync();
        var eventId = await SeedEventAsync();
        var failedId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO [OutboxMessages] ([Id], [Type], [AggregateId], [OccurredAtUtc], [PayloadJson], [DeduplicationKey], [Status], [AttemptCount], [NextAttemptAtUtc], [ProcessedAtUtc], [LastError], [CreatedAtUtc])
            VALUES ('{failedId}', 'roster.vacancy.opened.v1', '{eventId}', SYSUTCDATETIME(), N'[]', 'mig-1', 4, 8, SYSUTCDATETIME(), SYSUTCDATETIME(), N'boom', SYSUTCDATETIME()),
                   (NEWID(), 'roster.vacancy.opened.v1', '{eventId}', SYSUTCDATETIME(), N'[]', 'mig-2', 3, 1, SYSUTCDATETIME(), SYSUTCDATETIME(), NULL, SYSUTCDATETIME());
            INSERT INTO [InAppNotifications] ([Id], [PlayerId], [Type], [OccurrenceId], [SourceEventId], [Title], [Body], [CreatedAtUtc])
            VALUES ('{notificationId}', '{playerId}', 'roster.vacancy', '{eventId}', '{failedId}', N'Basketball spot opened', N'A participant spot opened in Wednesday Basketball.', SYSUTCDATETIME());
            """);

        await _db.MigrateToAsync(MigrationIds.AddWebPushAndOutboxOperations);

        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [OutboxMessages] WHERE [Id] = '{failedId}' AND [Status] = 4 AND [AttemptCount] = 8 AND [ReplayCount] = 0 AND [PriorAttemptCount] = 0 AND [LastReplayedAtUtc] IS NULL"))
            .Should().Be(1);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM [OutboxMessages]")).Should().Be(2);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [InAppNotifications] WHERE [Id] = '{notificationId}' AND [OpenedAtUtc] IS NULL")).Should().Be(1);
        foreach (var table in new[] { "PushSubscriptions", "PushDeliveries" })
            (await ScalarAsync<int>($"SELECT COUNT(*) FROM [{table}]")).Should().Be(0, $"nothing is backfilled into {table}");

        await _db.MigrateToAsync(MigrationIds.AddVacancyOutboxSubscriptionsAndNotifications);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE [name] IN (N'PushSubscriptions', N'PushDeliveries')")).Should().Be(0);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'OutboxMessages') AND [name] IN (N'ReplayCount', N'PriorAttemptCount', N'LastReplayedAtUtc')")).Should().Be(0);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM [OutboxMessages]")).Should().Be(2);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [InAppNotifications] WHERE [Id] = '{notificationId}'")).Should().Be(1);

        await _db.MigrateToAsync();
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE [name] IN (N'PushSubscriptions', N'PushDeliveries')")).Should().Be(2);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

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
