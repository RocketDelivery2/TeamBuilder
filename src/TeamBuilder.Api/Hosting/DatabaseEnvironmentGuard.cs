using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Api.Hosting;

/// <summary>Lets a background worker wait until it is safe to touch the database.</summary>
public interface IWorkerStartGate
{
    Task WaitAsync(CancellationToken cancellationToken);
}

/// <summary>A gate that is always open (no guard configured, and in unit tests).</summary>
public sealed class OpenWorkerStartGate : IWorkerStartGate
{
    public static readonly OpenWorkerStartGate Instance = new();

    public Task WaitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The outcome of comparing the database stamp with the expected environment.</summary>
public enum DatabaseEnvironmentState
{
    /// <summary>The check is off (Development, LocalQA, or explicitly disabled).</summary>
    NotRequired,
    Match,
    /// <summary>The database carries no stamp yet; run <c>database stamp-environment</c>.</summary>
    Missing,
    /// <summary>The database belongs to another environment (for example Production).</summary>
    Mismatch,
    Unreachable
}

/// <summary>
/// Keeps an API instance from working on another environment's database. Each deployed
/// database is stamped once with its environment name as a database-level extended property
/// (<see cref="PropertyName"/>), by <c>dotnet TeamBuilder.Api.dll database stamp-environment</c>.
/// Until the stamp matches, background workers (outbox, Web Push, retention, series
/// materialization) do not start and readiness reports Unhealthy, so a QA instance pointed at
/// the Production database by mistake never sends Production players a notification. Requests
/// themselves are not blocked: readiness keeps traffic away.
/// </summary>
public sealed class DatabaseEnvironmentGuard(
    IServiceScopeFactory scopeFactory,
    IOptions<DeploymentOptions> options,
    IHostEnvironment environment,
    ILogger<DatabaseEnvironmentGuard> logger) : IWorkerStartGate
{
    public const string PropertyName = "TeamBuilder.Environment";

    internal const string ReadStampSql =
        "SELECT CAST(value AS nvarchar(128)) AS [Value] FROM sys.extended_properties WHERE class = 0 AND name = N'TeamBuilder.Environment'";

    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(15);
    private volatile bool _verified;
    private int _mismatchLogged;

    public bool IsRequired => options.Value.VerifyDatabaseEnvironment ?? DeploymentEnvironments.IsDeployed(environment);

    public string ExpectedEnvironment =>
        string.IsNullOrWhiteSpace(options.Value.DatabaseEnvironment) ? environment.EnvironmentName : options.Value.DatabaseEnvironment.Trim();

    /// <summary>Reads the stamp (once verified, the answer is cached for the process lifetime).</summary>
    public async Task<DatabaseEnvironmentState> CheckAsync(CancellationToken cancellationToken)
    {
        if (!IsRequired)
            return DatabaseEnvironmentState.NotRequired;
        if (_verified)
            return DatabaseEnvironmentState.Match;

        string? stamp;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
            if (!db.Database.IsRelational())
                return DatabaseEnvironmentState.NotRequired;
            stamp = await ReadStampAsync(db, cancellationToken);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException)
        {
            logger.LogWarning("Could not read the database environment stamp: {Error}", ex.GetType().Name);
            return DatabaseEnvironmentState.Unreachable;
        }

        if (stamp is null)
            return DatabaseEnvironmentState.Missing;
        if (!string.Equals(stamp, ExpectedEnvironment, StringComparison.OrdinalIgnoreCase))
        {
            if (Interlocked.Exchange(ref _mismatchLogged, 1) == 0)
            {
                logger.LogCritical(
                    "The database is stamped for environment {DatabaseEnvironment} but this instance expects {ExpectedEnvironment}. Background workers will not start and readiness stays Unhealthy.",
                    stamp, ExpectedEnvironment);
            }
            return DatabaseEnvironmentState.Mismatch;
        }

        _verified = true;
        return DatabaseEnvironmentState.Match;
    }

    /// <summary>Blocks a worker until the stamp matches (re-checking periodically) or shutdown.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        var loggedWaiting = false;
        while (true)
        {
            var state = await CheckAsync(cancellationToken);
            if (state is DatabaseEnvironmentState.Match or DatabaseEnvironmentState.NotRequired)
                return;
            if (!loggedWaiting && state == DatabaseEnvironmentState.Missing)
            {
                logger.LogError(
                    "The database has no environment stamp; background workers wait until `database stamp-environment {ExpectedEnvironment}` has run.",
                    ExpectedEnvironment);
                loggedWaiting = true;
            }
            await Task.Delay(RetryInterval, cancellationToken);
        }
    }

    internal static async Task<string?> ReadStampAsync(TeamBuilderDbContext db, CancellationToken cancellationToken)
    {
        var values = await db.Database.SqlQueryRaw<string>(ReadStampSql).ToListAsync(cancellationToken);
        var value = values.FirstOrDefault();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Adds or replaces the stamp (requires ALTER permission on the database).</summary>
    internal static Task WriteStampAsync(TeamBuilderDbContext db, string environmentName, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            """
            IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 0 AND name = N'TeamBuilder.Environment')
                EXEC sys.sp_updateextendedproperty @name = N'TeamBuilder.Environment', @value = @environment;
            ELSE
                EXEC sys.sp_addextendedproperty @name = N'TeamBuilder.Environment', @value = @environment;
            """,
            [new SqlParameter("@environment", System.Data.SqlDbType.NVarChar, 128) { Value = environmentName }],
            cancellationToken);
}
