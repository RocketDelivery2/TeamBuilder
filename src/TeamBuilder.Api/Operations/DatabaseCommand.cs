using Microsoft.EntityFrameworkCore;
using TeamBuilder.Api.Hosting;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Api.Operations;

/// <summary>
/// Release operations against the configured database, without starting the web host:
/// <c>dotnet TeamBuilder.Api.dll database &lt;command&gt;</c>. Schema changes are applied only by
/// the EF Core migration bundle (docs/qa/private-qa-deployment.md); this command reports on
/// them and manages the environment stamp that <see cref="DatabaseEnvironmentGuard"/> checks.
/// It prints migration ids and environment names only, never the connection string.
/// </summary>
public static class DatabaseCommand
{
    public const string Name = "database";

    public const string Usage = """
        Usage: dotnet TeamBuilder.Api.dll database <command>
          status                       Applied and pending migrations; exit code 3 when any are pending.
          show-environment             The environment this database is stamped for.
          stamp-environment <name>     Stamp this database for an environment (QA, Production, ...).
                                       Run once per database, after the first migration bundle.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var command = args.Length > 0 ? args[0] : "";
        if (command is not ("status" or "show-environment" or "stamp-environment"))
        {
            await output.WriteLineAsync(Usage);
            return command is "" or "help" or "--help" ? 0 : 2;
        }

        var connectionString = configuration.GetConnectionString("TeamBuilderSql");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await output.WriteLineAsync("ConnectionStrings:TeamBuilderSql is not configured.");
            return 2;
        }

        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>().UseSqlServer(connectionString).Options;
        await using var db = new TeamBuilderDbContext(options);

        switch (command)
        {
            case "status":
            {
                var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
                var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
                var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
                await output.WriteLineAsync($"Applied {applied.Count}  latest {applied.LastOrDefault() ?? "(none)"}");
                await output.WriteLineAsync($"Pending {pending.Count}");
                foreach (var id in pending)
                    await output.WriteLineAsync($"  pending {id}");
                foreach (var id in applied.Where(id => !known.Contains(id)))
                    await output.WriteLineAsync($"  newer than this build {id}");
                return pending.Count == 0 ? 0 : 3;
            }
            case "show-environment":
            {
                var stamp = await DatabaseEnvironmentGuard.ReadStampAsync(db, cancellationToken);
                await output.WriteLineAsync(stamp is null ? "Not stamped." : $"Stamped for {stamp}.");
                return stamp is null ? 3 : 0;
            }
            default:
            {
                var environment = args.Length > 1 ? args[1].Trim() : "";
                if (environment.Length is 0 or > 128 || environment.Any(char.IsControl))
                {
                    await output.WriteLineAsync("Name the environment, for example: database stamp-environment QA");
                    return 2;
                }

                var previous = await DatabaseEnvironmentGuard.ReadStampAsync(db, cancellationToken);
                if (previous is not null && !string.Equals(previous, environment, StringComparison.OrdinalIgnoreCase) &&
                    !args.Contains("--force"))
                {
                    await output.WriteLineAsync($"This database is stamped for {previous}. Re-stamping it for {environment} needs --force.");
                    return 4;
                }

                await DatabaseEnvironmentGuard.WriteStampAsync(db, environment, cancellationToken);
                await output.WriteLineAsync($"Stamped for {environment}.");
                return 0;
            }
        }
    }
}
