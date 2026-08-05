using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// A single, uniquely named database inside the shared <see cref="SqlServerContainerFixture"/>
/// container. Each integration test creates its own instance so tests never observe each
/// other's rows and can run in any order/in parallel within the same container.
/// </summary>
public sealed class SqlServerTestDatabase : IAsyncDisposable
{
    public string DatabaseName { get; }
    public string ConnectionString { get; }

    public SqlServerTestDatabase(SqlServerContainerFixture fixture, string namePrefix)
    {
        DatabaseName = SqlServerContainerFixture.NewDatabaseName(namePrefix);
        ConnectionString = fixture.CreateConnectionString(DatabaseName);
    }

    public TeamBuilderDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        return new TeamBuilderDbContext(options);
    }

    /// <summary>
    /// Applies migrations up to and including <paramref name="targetMigrationId"/> (the exact
    /// migration id, e.g. "20260511064428_InitialCreate"), or all migrations when null. The
    /// target database (and, along the way, every intermediate migration) is created by EF
    /// Core's real SQL Server migrator - the same code path used in production.
    /// </summary>
    public async Task MigrateToAsync(string? targetMigrationId = null)
    {
        await using var context = CreateContext();
        var migrator = context.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigrationId);
    }

    public async ValueTask DisposeAsync()
    {
        // Drop the uniquely named database from master so the shared container doesn't
        // accumulate databases across the whole test run. Each test uses a GUID-suffixed
        // name, so this never races with another test's database.
        var masterConnectionString = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" }.ConnectionString;

        await using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF DB_ID(N'{DatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{DatabaseName}];
            END
            """;
        await command.ExecuteNonQueryAsync();
    }
}
