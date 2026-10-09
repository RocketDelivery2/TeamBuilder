using Microsoft.Extensions.Options;
using TeamBuilder.Infrastructure.Outbox;

namespace TeamBuilder.Api.Workers;

/// <summary>
/// Runs <see cref="OutboxMaintenance.PurgeAsync"/> every <see cref="OutboxMaintenanceOptions.Interval"/>
/// (first pass one interval after startup). Several instances may run it at once: the bounded
/// READPAST deletes make that harmless.
/// </summary>
public sealed class OutboxMaintenanceWorker : BackgroundService
{
    private readonly OutboxMaintenance _maintenance;
    private readonly TimeProvider _timeProvider;
    private readonly OutboxMaintenanceOptions _options;
    private readonly ILogger<OutboxMaintenanceWorker> _logger;

    public OutboxMaintenanceWorker(OutboxMaintenance maintenance, TimeProvider timeProvider, IOptions<OutboxMaintenanceOptions> options, ILogger<OutboxMaintenanceWorker> logger)
    {
        _maintenance = maintenance;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Outbox retention is disabled; completed rows are kept.");
            return;
        }

        while (true)
        {
            await Task.Delay(_options.Interval, _timeProvider, stoppingToken);
            try
            {
                await _maintenance.PurgeAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Outbox retention pass failed; it runs again next interval.");
            }
        }
    }
}
