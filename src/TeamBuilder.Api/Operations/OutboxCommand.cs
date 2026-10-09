using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Outbox;
using TeamBuilder.Infrastructure.WebPush;

namespace TeamBuilder.Api.Operations;

/// <summary>
/// Operator maintenance command of the API binary, for environments without an admin role:
/// <c>dotnet TeamBuilder.Api.dll outbox &lt;command&gt;</c> (in the QA stack:
/// <c>docker compose -f docker-compose.qa.yml exec api dotnet TeamBuilder.Api.dll outbox status</c>).
/// It uses the same configuration (appsettings, environment variables) as the API and talks to
/// the database directly; there is no HTTP surface. Output carries ids, types, counts and
/// truncated error text only, never payloads, push credentials or personal data.
/// </summary>
public static class OutboxCommand
{
    public const string Name = "outbox";

    public const string Usage = """
        Usage: dotnet TeamBuilder.Api.dll outbox <command>
          status             Count outbox messages by status.
          failed [take]      List Failed messages (metadata only, newest first; default 50).
          replay <id>        Requeue one Failed message (idempotent; keeps its attempt history).
          purge              Run one retention pass now (OutboxMaintenance settings).
          vapid-keys         Generate a new VAPID key pair for WebPush configuration.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var command = args.Length > 0 ? args[0] : "";
        if (command == "vapid-keys")
        {
            // Needs no database. The private key is a secret: store it in the deployment's
            // secret store / .env, never in a committed file.
            var (publicKey, privateKey) = VapidKeys.Generate();
            await output.WriteLineAsync($"WebPush__VapidPublicKey={publicKey}");
            await output.WriteLineAsync($"WebPush__VapidPrivateKey={privateKey}");
            return 0;
        }

        if (command is not ("status" or "failed" or "replay" or "purge"))
        {
            await output.WriteLineAsync(Usage);
            return command is "" or "help" or "--help" ? 0 : 2;
        }

        var services = new ServiceCollection();
        // Diagnostics go to stderr so stdout carries only the command's own output.
        services.AddLogging(logging => logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace).SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<TeamBuilderDbContext>(options => options.UseSqlServer(configuration.GetConnectionString("TeamBuilderSql")));
        services.AddOptions<OutboxMaintenanceOptions>().Bind(configuration.GetSection(OutboxMaintenanceOptions.SectionName)).ValidateDataAnnotations();
        services.AddScoped<OutboxOperations>();
        services.AddSingleton<OutboxMaintenance>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var operations = scope.ServiceProvider.GetRequiredService<OutboxOperations>();

        switch (command)
        {
            case "status":
            {
                var counts = await operations.CountAsync(cancellationToken);
                await output.WriteLineAsync($"Pending {counts.Pending}  Processing {counts.Processing}  Completed {counts.Completed}  Failed {counts.Failed}");
                return 0;
            }
            case "failed":
            {
                var take = args.Length > 1 && int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 50;
                var failed = await operations.ListFailedAsync(take, cancellationToken);
                if (failed.Count == 0)
                    await output.WriteLineAsync("No failed outbox messages.");
                foreach (var m in failed)
                {
                    await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"{m.Id}  {m.Type}  aggregate {m.AggregateId}  occurred {m.OccurredAtUtc:O}  failed {m.FailedAtUtc:O}  attempts {m.AttemptCount} (+{m.PriorAttemptCount} before {m.ReplayCount} replay(s))"));
                    if (!string.IsNullOrEmpty(m.LastError))
                        await output.WriteLineAsync($"    {m.LastError}");
                }
                return 0;
            }
            case "replay":
            {
                if (args.Length < 2 || !Guid.TryParse(args[1], out var id))
                {
                    await output.WriteLineAsync("replay needs a message id (see: outbox failed).");
                    return 2;
                }
                var result = await operations.ReplayAsync(id, cancellationToken);
                await output.WriteLineAsync(result switch
                {
                    OutboxReplayResult.Requeued => $"Requeued {id}; a worker will process it on its next poll.",
                    OutboxReplayResult.NotFound => $"No outbox message {id}.",
                    _ => $"Message {id} is not Failed; nothing to replay."
                });
                return result == OutboxReplayResult.NotFound ? 1 : 0;
            }
            default:
            {
                var purged = await scope.ServiceProvider.GetRequiredService<OutboxMaintenance>().PurgeAsync(cancellationToken);
                await output.WriteLineAsync(
                    $"Purged {purged.CompletedMessages} completed and {purged.FailedMessages} failed message(s), {purged.PushDeliveries} push delivery(ies), {purged.DisabledPushSubscriptions} disabled push subscription(s).");
                return 0;
            }
        }
    }
}
