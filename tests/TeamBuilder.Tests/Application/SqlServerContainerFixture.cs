using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Starts one disposable SQL Server container (image: mcr.microsoft.com/mssql/server:2022-latest)
/// for the whole real-SQL-Server integration test collection, and disposes it even if a test
/// throws. Tests do not depend on execution order: each test creates and migrates its own
/// uniquely named database inside the shared container so tests never see each other's rows.
///
/// No connection string is hard-coded: Testcontainers generates a random container-scoped
/// SA password and publishes the container's dynamically mapped host port; both are only ever
/// read from the container instance at run time, never logged or persisted.
/// </summary>
public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>
    /// Builds a connection string to a fresh, uniquely named database on the shared container.
    /// The database itself is created by the caller (typically via Database.MigrateAsync,
    /// which creates the database as part of applying the first migration).
    /// </summary>
    public string CreateConnectionString(string databaseName)
    {
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = databaseName
        };

        return builder.ConnectionString;
    }

    public static string NewDatabaseName(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    public TeamBuilderDbContext CreateContext(string databaseName)
    {
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseSqlServer(CreateConnectionString(databaseName))
            .Options;

        return new TeamBuilderDbContext(options);
    }
}

/// <summary>
/// xUnit collection definition so all real-SQL-Server tests share a single container instance
/// (one docker container start/stop per test run) instead of one container per test class.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerContainerFixture>
{
    public const string Name = "SqlServer";
}
