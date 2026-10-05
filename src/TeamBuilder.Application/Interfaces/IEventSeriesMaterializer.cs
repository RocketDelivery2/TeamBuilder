namespace TeamBuilder.Application.Interfaces;

/// <summary>
/// Materializes concrete occurrences of a recurring series. Reusable by series creation and by a
/// future rolling-horizon worker. Idempotent: only missing occurrences are inserted, and the
/// UX_Events_SeriesId_ScheduledStartUtc unique index is the final guard against duplicates.
/// </summary>
public interface IEventSeriesMaterializer
{
    /// <summary>The initial horizon: local dates from the series start through start + 20 days.</summary>
    public const int InitialHorizonDays = 21;

    /// <summary>
    /// Inserts the series' missing occurrences whose local date is on or before
    /// <paramref name="throughLocalDate"/> (bounded by the series end date). Returns the number
    /// inserted by this call; 0 when the series is missing or not active, or when a concurrent
    /// materialization inserted the same occurrences first.
    /// </summary>
    Task<int> MaterializeAsync(Guid seriesId, DateOnly throughLocalDate, CancellationToken cancellationToken = default);
}
