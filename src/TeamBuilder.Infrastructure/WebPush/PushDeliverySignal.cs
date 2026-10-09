namespace TeamBuilder.Infrastructure.WebPush;

/// <summary>
/// In-process wake-up for the push dispatcher: the vacancy handler signals after committing
/// deliveries, so a push leaves at once instead of after the next poll. Other instances still
/// find the rows by polling; a lost signal only costs one poll interval.
/// </summary>
public sealed class PushDeliverySignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Signal()
    {
        try
        {
            if (_semaphore.CurrentCount == 0)
                _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
    }

    /// <summary>Waits for a signal or the timeout, whichever comes first.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _semaphore.WaitAsync(timeout, cancellationToken);
}
