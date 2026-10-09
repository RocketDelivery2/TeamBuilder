using TeamBuilder.Api.Hosting;
using Microsoft.Extensions.Options;
using TeamBuilder.Infrastructure.WebPush;

namespace TeamBuilder.Api.Workers;

/// <summary>
/// Sends queued Web Push deliveries: loops <see cref="PushDispatcher.ProcessBatchAsync"/>,
/// draining at once while batches are full and otherwise waiting for the in-process signal
/// from the vacancy handler or the poll interval, whichever comes first. Does nothing when
/// Web Push or its dispatcher is disabled. Safe on every instance (leases, as for the outbox).
/// </summary>
public sealed class PushDeliveryWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly PushDeliverySignal _signal;
    private readonly WebPushOptions _options;
    private readonly ILogger<PushDeliveryWorker> _logger;
    private readonly IWorkerStartGate _startGate;

    public PushDeliveryWorker(IServiceProvider services, PushDeliverySignal signal, IOptions<WebPushOptions> options, ILogger<PushDeliveryWorker> logger, IWorkerStartGate? startGate = null)
    {
        _services = services;
        _signal = signal;
        _options = options.Value;
        _logger = logger;
        _startGate = startGate ?? OpenWorkerStartGate.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.DispatcherEnabled)
        {
            _logger.LogInformation("Web Push delivery is disabled; in-app notifications are unaffected.");
            return;
        }

        // Never touch another environment's database (see DatabaseEnvironmentGuard).
        await _startGate.WaitAsync(stoppingToken);

        var dispatcher = _services.GetRequiredService<PushDispatcher>();
        while (true)
        {
            var drainAgain = false;
            try
            {
                var batch = await dispatcher.ProcessBatchAsync(stoppingToken);
                drainAgain = batch.Claimed >= _options.BatchSize;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError("Push delivery batch failed: {ExceptionType}.", ex.GetType().Name);
            }

            if (!drainAgain)
                await _signal.WaitAsync(_options.PollInterval, stoppingToken);
        }
    }
}
