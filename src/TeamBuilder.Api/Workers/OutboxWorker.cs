using Microsoft.Extensions.Options;
using TeamBuilder.Infrastructure.Outbox;

namespace TeamBuilder.Api.Workers;

/// <summary>
/// Drains the transactional outbox: repeatedly calls <see cref="OutboxProcessor.ProcessBatchAsync"/>.
/// A full batch means more is waiting, so the next batch is claimed at once; otherwise the
/// worker sleeps for the poll interval (cancelled by shutdown), so an idle outbox costs one
/// small indexed query per interval and never spins. Safe to run on every instance: claiming is
/// exclusive per message (see <see cref="OutboxProcessor"/>).
/// </summary>
public sealed class OutboxWorker : BackgroundService
{
    private readonly OutboxProcessor _processor;
    private readonly TimeProvider _timeProvider;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxWorker> _logger;

    public OutboxWorker(OutboxProcessor processor, TimeProvider timeProvider, IOptions<OutboxOptions> options, ILogger<OutboxWorker> logger)
    {
        _processor = processor;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Outbox worker is disabled; outbox messages stay pending.");
            return;
        }

        while (true)
        {
            var drainAgain = false;
            try
            {
                var batch = await _processor.ProcessBatchAsync(stoppingToken);
                drainAgain = batch.Claimed >= _options.BatchSize;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // The database may be unreachable; wait a full interval and try again.
                _logger.LogError(ex, "Outbox batch failed.");
            }

            if (!drainAgain)
                await Task.Delay(_options.PollInterval, _timeProvider, stoppingToken);
        }
    }
}
