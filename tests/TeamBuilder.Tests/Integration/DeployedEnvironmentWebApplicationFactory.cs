using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// The API as a deployed environment (QA by default) over a real SQL Server database: OIDC
/// authority configuration instead of a developer signing key, no startup migration, and the
/// real readiness checks. The authority is a placeholder that is never contacted (metadata is
/// only fetched for a bearer token), so these tests cover startup, health and anonymous routes.
/// </summary>
public sealed class DeployedEnvironmentWebApplicationFactory(
    string connectionString,
    string environmentName = "QA",
    IReadOnlyDictionary<string, string?>? settings = null) : TeamBuilderWebApplicationFactory
{
    public const string Authority = "https://login.example.test/qa/v2.0";

    protected override bool KeepHealthChecks => true;

    protected override void ConfigureDatabase(DbContextOptionsBuilder options) => options.UseSqlServer(connectionString);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings:TeamBuilderSql"] = connectionString,
                ["Jwt:SigningKey"] = "",
                ["Jwt:Issuer"] = "",
                ["Jwt:Authority"] = Authority,
                ["Jwt:Audience"] = "api://teambuilder-qa",
                ["Jwt:RequireHttpsMetadata"] = "true",
                ["AllowedOrigins"] = "https://qa.teambuilder.example",
                ["Database:ApplyMigrationsOnStartup"] = "false",
            };
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
                values[key] = value;
            config.AddInMemoryCollection(values);
        });
        builder.UseEnvironment(environmentName);
    }
}
