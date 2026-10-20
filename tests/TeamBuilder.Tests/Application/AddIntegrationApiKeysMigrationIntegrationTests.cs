using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Infrastructure.Data.Configurations;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves the AddIntegrationApiKeys migration on a real SQL Server 2022 database: on an empty
/// database (the key table with its unique prefix, owner index, binary prefix collation and
/// cascading owner FK; the host start-window index replacing IX_Events_HostId; no pending model
/// changes), and on a database at the previous (#179) migration whose hosted games survive Up,
/// Down and Up again.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class AddIntegrationApiKeysMigrationIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public AddIntegrationApiKeysMigrationIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "apikeymig");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task EmptyDatabase_GetsTheApiKeySchema_WithNoPendingModelChanges()
    {
        await _db.MigrateToAsync();

        (await IndexAsync(IntegrationApiKeyConfiguration.TableName, IntegrationApiKeyConfiguration.KeyPrefixUniqueIndexName))
            .Should().Be((true, null, "KeyPrefix", ""));
        (await IndexAsync(IntegrationApiKeyConfiguration.TableName, IntegrationApiKeyConfiguration.PlayerIndexName))
            .Should().Be((false, null, "PlayerId,CreatedAtUtc", ""));
        (await ForeignKeyActionAsync(IntegrationApiKeyConfiguration.PlayerForeignKeyName)).Should().Be("CASCADE");
        (await ScalarAsync<string>("SELECT [collation_name] FROM sys.columns WHERE [object_id] = OBJECT_ID(N'IntegrationApiKeys') AND [name] = N'KeyPrefix'"))
            .Should().Be("Latin1_General_100_BIN2");
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'IntegrationApiKeys') AND [name] IN (N'Key', N'Secret', N'Plaintext')"))
            .Should().Be(0, "only a hash of the key is stored");

        (await IndexAsync(EventOccurrenceConfiguration.TableName, EventOccurrenceConfiguration.HostStartIndexName))
            .Should().Be((false, null, "HostId,ScheduledStartUtc", ""));
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'Events') AND [name] = N'IX_Events_HostId'"))
            .Should().Be(0, "the composite index leads with HostId and serves the foreign key");

        await using var context = _db.CreateContext();
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public async Task HostedGames_SurviveUp_AndDownThenUpWorks()
    {
        await _db.MigrateToAsync(MigrationIds.AddWebPushAndOutboxOperations);
        var hostId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO [Players] ([Id], [Username], [CreatedAtUtc]) VALUES ('{hostId}', N'host{hostId:N}', SYSUTCDATETIME());
            INSERT INTO [Events] ([Id], [Name], [ScheduledStartUtc], [Status], [MaxParticipants], [CurrentParticipantCount], [IsDetached], [Category], [HostId], [CreatedAtUtc])
            VALUES ('{eventId}', N'Wednesday Basketball', '2026-10-22T00:00:00', 2, 10, 0, 0, N'basketball', '{hostId}', SYSUTCDATETIME());
            """);

        await _db.MigrateToAsync(MigrationIds.AddIntegrationApiKeys);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [Events] WHERE [Id] = '{eventId}' AND [HostId] = '{hostId}'")).Should().Be(1);
        await ExecuteAsync($"""
            INSERT INTO [IntegrationApiKeys] ([Id], [PlayerId], [Name], [KeyPrefix], [SecretHash], [Scopes], [CreatedAtUtc])
            VALUES (NEWID(), '{hostId}', N'League site', '0123456789ab', 0x{new string('a', 64)}, 'rosters:read', SYSUTCDATETIME());
            """);
        var duplicatePrefix = async () => await ExecuteAsync($"""
            INSERT INTO [IntegrationApiKeys] ([Id], [PlayerId], [Name], [KeyPrefix], [SecretHash], [Scopes], [CreatedAtUtc])
            VALUES (NEWID(), '{hostId}', N'Other', '0123456789ab', 0x{new string('b', 64)}, 'rosters:read', SYSUTCDATETIME());
            """);
        await duplicatePrefix.Should().ThrowAsync<SqlException>();

        await _db.MigrateToAsync(MigrationIds.AddWebPushAndOutboxOperations);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE [name] = N'IntegrationApiKeys'")).Should().Be(0);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'Events') AND [name] = N'IX_Events_HostId'")).Should().Be(1);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [Events] WHERE [Id] = '{eventId}'")).Should().Be(1);

        await _db.MigrateToAsync();
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE [name] = N'IntegrationApiKeys'")).Should().Be(1);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

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
