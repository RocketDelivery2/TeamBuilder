using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Api.Hosting;
using TeamBuilder.Api.Operations;
using TeamBuilder.Tests.Application;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// The deployed (QA) API on real SQL Server: it starts with startup migrations disabled against
/// a database the migration path already brought up to date, and readiness reports the schema
/// and environment stamp truthfully. It never migrates a database itself, refuses to start when
/// told to, stays ready on a schema a newer build extended (rollback to the previous image), and
/// is not ready on another environment's database.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class DeploymentReadinessSqlServerIntegrationTests(SqlServerContainerFixture fixture)
{
    [Fact]
    public async Task MigratedAndStampedDatabase_StartsWithoutStartupMigrations_AndIsReady()
    {
        await using var database = new SqlServerTestDatabase(fixture, "deploy_ready");
        await database.MigrateToAsync();
        await StampAsync(database, "QA");
        var historyBefore = await HistoryCountAsync(database);

        await using var factory = new DeployedEnvironmentWebApplicationFactory(database.ConnectionString);
        using var client = factory.CreateClient();

        var (liveStatus, live) = await GetJsonAsync(client, "/healthz/live");
        liveStatus.Should().Be(HttpStatusCode.OK);
        live.GetProperty("status").GetString().Should().Be("Healthy");
        live.GetProperty("version").GetString().Should().NotBeNullOrEmpty();

        var (readyStatus, ready) = await GetJsonAsync(client, "/healthz/ready");
        readyStatus.Should().Be(HttpStatusCode.OK, ready.ToString());
        ready.GetProperty("checks").GetProperty("database").GetString().Should().Be("Healthy");
        ready.GetProperty("checks").GetProperty("environment").GetString().Should().Be("Healthy");
        ready.GetProperty("checks").GetProperty("configuration").GetString().Should().Be("Healthy");

        (await HistoryCountAsync(database)).Should().Be(historyBefore, "the API never changes the schema");
    }

    [Fact]
    public async Task EmptyDatabase_IsLiveButNotReady_AndIsNotMigratedByTheApi()
    {
        await using var database = new SqlServerTestDatabase(fixture, "deploy_empty");
        await CreateEmptyDatabaseAsync(database);
        await StampAsync(database, "QA");

        await using var factory = new DeployedEnvironmentWebApplicationFactory(database.ConnectionString);
        using var client = factory.CreateClient();

        (await client.GetAsync("/healthz/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        var (status, ready) = await GetJsonAsync(client, "/healthz/ready");
        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        ready.GetProperty("checks").GetProperty("database").GetString().Should().Be("Unhealthy");
        (await TableExistsAsync(database, "__EFMigrationsHistory")).Should().BeFalse();
    }

    [Fact]
    public async Task OneMigrationBehind_IsNotReady()
    {
        await using var database = new SqlServerTestDatabase(fixture, "deploy_behind");
        await using (var context = database.CreateContext())
        {
            var all = context.Database.GetMigrations().ToList();
            await database.MigrateToAsync(all[^2]);
        }
        await StampAsync(database, "QA");

        await using var factory = new DeployedEnvironmentWebApplicationFactory(database.ConnectionString);
        using var client = factory.CreateClient();

        var (status, ready) = await GetJsonAsync(client, "/healthz/ready");
        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        ready.GetProperty("checks").GetProperty("database").GetString().Should().Be("Unhealthy");
    }

    [Fact]
    public async Task SchemaAheadOfThisBuild_StaysReady_SoThePreviousImageCanServeAfterARollback()
    {
        await using var database = new SqlServerTestDatabase(fixture, "deploy_ahead");
        await database.MigrateToAsync();
        await StampAsync(database, "QA");
        await ExecuteAsync(database,
            "INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES (N'29991231000000_FromANewerBuild', N'10.0.12')");

        await using var factory = new DeployedEnvironmentWebApplicationFactory(database.ConnectionString);
        using var client = factory.CreateClient();

        (await client.GetAsync("/healthz/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Production")]
    public async Task DatabaseOfAnotherEnvironment_IsNotReady_AndWorkersStayParked(string? stamp)
    {
        await using var database = new SqlServerTestDatabase(fixture, "deploy_stamp");
        await database.MigrateToAsync();
        if (stamp is not null)
            await StampAsync(database, stamp);

        await using var factory = new DeployedEnvironmentWebApplicationFactory(database.ConnectionString);
        using var client = factory.CreateClient();

        var (status, ready) = await GetJsonAsync(client, "/healthz/ready");
        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        ready.GetProperty("checks").GetProperty("environment").GetString().Should().Be("Unhealthy");

        var guard = factory.Services.GetRequiredService<DatabaseEnvironmentGuard>();
        (await guard.CheckAsync(CancellationToken.None)).Should().Be(stamp is null ? DatabaseEnvironmentState.Missing : DatabaseEnvironmentState.Mismatch);
        var gate = factory.Services.GetRequiredService<IWorkerStartGate>().WaitAsync(CancellationToken.None);
        (await Task.WhenAny(gate, Task.Delay(TimeSpan.FromSeconds(1)))).Should().NotBeSameAs(gate, "workers wait until the stamp matches");

        // Stamping the database for this environment opens readiness without a restart.
        await StampAsync(database, "QA", force: true);
        (await client.GetAsync("/healthz/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task StartupMigrationRequestedInQa_RefusesToStart_AndLeavesTheDatabaseAlone()
    {
        await using var database = new SqlServerTestDatabase(fixture, "deploy_refuse");
        await CreateEmptyDatabaseAsync(database);

        await using var factory = new DeployedEnvironmentWebApplicationFactory(database.ConnectionString,
            settings: new Dictionary<string, string?> { ["Database:ApplyMigrationsOnStartup"] = "true" });

        var start = () => factory.CreateClient();
        start.Should().Throw<InvalidOperationException>().WithMessage("*Refusing to start in QA*Database:ApplyMigrationsOnStartup*");
        (await TableExistsAsync(database, "__EFMigrationsHistory")).Should().BeFalse();
    }

    [Fact]
    public async Task DatabaseCommand_ReportsPendingMigrations_AndStampsOnce()
    {
        await using var database = new SqlServerTestDatabase(fixture, "deploy_cli");
        await using (var context = database.CreateContext())
            await database.MigrateToAsync(context.Database.GetMigrations().First());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TeamBuilderSql"] = database.ConnectionString })
            .Build();

        var output = new StringWriter();
        (await DatabaseCommand.RunAsync(["status"], output, configuration)).Should().Be(3);
        output.ToString().Should().Contain("Pending").And.NotContain("Password");

        await database.MigrateToAsync();
        (await DatabaseCommand.RunAsync(["status"], new StringWriter(), configuration)).Should().Be(0);

        (await DatabaseCommand.RunAsync(["show-environment"], new StringWriter(), configuration)).Should().Be(3);
        (await DatabaseCommand.RunAsync(["stamp-environment", "QA"], new StringWriter(), configuration)).Should().Be(0);
        (await DatabaseCommand.RunAsync(["stamp-environment", "QA"], new StringWriter(), configuration)).Should().Be(0, "re-stamping the same environment is idempotent");
        var refused = new StringWriter();
        (await DatabaseCommand.RunAsync(["stamp-environment", "Production"], refused, configuration)).Should().Be(4);
        refused.ToString().Should().Contain("--force");
        var shown = new StringWriter();
        (await DatabaseCommand.RunAsync(["show-environment"], shown, configuration)).Should().Be(0);
        shown.ToString().Should().Contain("Stamped for QA.");
    }

    private static async Task StampAsync(SqlServerTestDatabase database, string environment, bool force = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TeamBuilderSql"] = database.ConnectionString })
            .Build();
        string[] args = force ? ["stamp-environment", environment, "--force"] : ["stamp-environment", environment];
        (await DatabaseCommand.RunAsync(args, new StringWriter(), configuration)).Should().Be(0);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetJsonAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, document.RootElement.Clone());
    }

    private static async Task CreateEmptyDatabaseAsync(SqlServerTestDatabase database)
    {
        var master = new SqlConnectionStringBuilder(database.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{database.DatabaseName}]";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> HistoryCountAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateContext();
        return (await context.Database.GetAppliedMigrationsAsync()).Count();
    }

    private static async Task<bool> TableExistsAsync(SqlServerTestDatabase database, string table)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name = @name";
        command.Parameters.AddWithValue("@name", table);
        return (int)(await command.ExecuteScalarAsync())! > 0;
    }

    private static async Task ExecuteAsync(SqlServerTestDatabase database, string sql)
    {
        await using var context = database.CreateContext();
        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
