using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Api.Hosting;

/// <summary>
/// Liveness and readiness for containers and load balancers:
/// <list type="bullet">
/// <item><c>GET /healthz/live</c>: the process is up and serving. Runs no check and touches no
/// dependency, so a slow database never restarts a healthy instance.</item>
/// <item><c>GET /healthz/ready</c>: this instance should get traffic. The database answers a
/// one-row query, its schema has every migration this build knows (the migration bundle ran),
/// it is stamped for this environment, and the deployment configuration is valid. Web Push is
/// deliberately not part of it: a push service outage degrades alerts, never the API.</item>
/// </list>
/// Both answer JSON with the overall status, each check's status, and the build version and
/// commit. Exception text and descriptions are never returned. The older <c>/health</c> and
/// <c>/health/ready</c> paths remain as plain-text aliases.
/// </summary>
public static class HealthEndpoints
{
    public const string ReadyTag = "ready";
    public const string LivePath = "/healthz/live";
    public const string ReadyPath = "/healthz/ready";

    public static IHealthChecksBuilder AddTeamBuilderHealthChecks(this IServiceCollection services) =>
        services.AddHealthChecks()
            .AddCheck<DatabaseSchemaHealthCheck>("database", tags: [ReadyTag])
            .AddCheck<DatabaseEnvironmentHealthCheck>("environment", tags: [ReadyTag])
            .AddCheck<DeploymentConfigurationHealthCheck>("configuration", tags: [ReadyTag]);

    public static void MapTeamBuilderHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteJsonAsync })
            .AllowAnonymous().DisableRateLimiting();
        app.MapHealthChecks(ReadyPath, new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag), ResponseWriter = WriteJsonAsync })
            .AllowAnonymous().DisableRateLimiting();

        // Original plain-text paths, kept for existing probes and docs.
        app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) }).AllowAnonymous();
    }

    private static Task WriteJsonAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        var body = new
        {
            status = report.Status.ToString(),
            version = BuildInfo.Version,
            commit = BuildInfo.Commit,
            checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString())
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// The database is reachable and at least at this build's latest migration. A database that
/// is ahead (a newer build's additive migration, during a rollback to the previous image) is
/// still Healthy, which is what lets the previous compatible image serve.
/// </summary>
internal sealed class DatabaseSchemaHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        if (!db.Database.IsRelational())
            return HealthCheckResult.Healthy();

        try
        {
            db.Database.SetCommandTimeout(5);
            // Every migration this build knows must be applied. Extra applied migrations (a newer
            // build's) are fine and do not count as pending.
            var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
            if (pending.Any())
                return HealthCheckResult.Unhealthy("The database schema is behind this build; apply the migration bundle.");
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unreachable, login failed, or no migrations table at all (an empty database).
            return HealthCheckResult.Unhealthy("The database is unreachable or not migrated.", ex);
        }
    }
}

/// <summary>Unhealthy until the database stamp matches this environment (see <see cref="DatabaseEnvironmentGuard"/>).</summary>
internal sealed class DatabaseEnvironmentHealthCheck(DatabaseEnvironmentGuard guard) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await guard.CheckAsync(cancellationToken) switch
        {
            DatabaseEnvironmentState.Match or DatabaseEnvironmentState.NotRequired => HealthCheckResult.Healthy(),
            var state => HealthCheckResult.Unhealthy($"Database environment stamp: {state}.")
        };
}

/// <summary>The deployment configuration rules still hold (they are also enforced at startup).</summary>
internal sealed class DeploymentConfigurationHealthCheck(IConfiguration configuration, IHostEnvironment environment) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(DeploymentConfigurationValidator.Validate(configuration, environment).Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Deployment configuration is invalid."));
}
