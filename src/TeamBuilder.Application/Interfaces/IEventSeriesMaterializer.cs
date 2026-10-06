namespace TeamBuilder.Application.Interfaces;

/// <summary>
/// Materializes concrete occurrences of a recurring series and maintains its
/// <c>MaterializedThroughLocalDate</c> checkpoint. Used by series creation (through the shared
/// generator) and by the rolling-horizon worker. Idempotent: only missing occurrences are
/// inserted, and the UX_Events_SeriesId_ScheduledStartUtc unique index is the final guard
/// against duplicates. Inserted occurrences and the checkpoint commit in one transaction,
/// guarded by the series RowVersion, so nothing is created for a series that stopped being
/// Active before the commit.
/// </summary>
public interface IEventSeriesMaterializer
{
    /// <summary>The initial and rolling horizon: 21 local calendar days (today .. today + 20).</summary>
    public const int InitialHorizonDays = 21;

    /// <summary>The rolling horizon kept ahead of today in each series' time zone.</summary>
    public const int RollingHorizonDays = InitialHorizonDays;

    /// <summary>
    /// Inserts the series' missing occurrences whose local date is within
    /// <paramref name="fromLocalDate"/>..<paramref name="throughLocalDate"/> (inclusive, bounded by
    /// the series start and end dates). Occurrence indices are those full generation from the
    /// series start would assign. Dates before today (in the series time zone) are never
    /// materialized by the rolling contract, so the checkpoint advances to the bounded
    /// <paramref name="throughLocalDate"/> whenever the range reaches back to
    /// max(series start, today, checkpoint + 1).
    /// </summary>
    Task<EventSeriesMaterializationResult> MaterializeAsync(
        Guid seriesId,
        DateOnly fromLocalDate,
        DateOnly throughLocalDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Keeps an Active series materialized through today + 20 days in its own time zone (bounded
    /// by the series end date), resuming after the checkpoint. Does nothing when the series is
    /// missing, not Active, ended, or already materialized through the target.
    /// </summary>
    Task<EventSeriesMaterializationResult> MaintainHorizonAsync(Guid seriesId, CancellationToken cancellationToken = default);
}

public enum EventSeriesMaterializationOutcome
{
    /// <summary>Occurrences and/or the checkpoint were committed by this call.</summary>
    Materialized = 1,

    /// <summary>The checkpoint already covers the target; nothing to do.</summary>
    UpToDate = 2,

    /// <summary>The series does not exist.</summary>
    NotFound = 3,

    /// <summary>The series is not Active (Paused, Cancelled or Completed), possibly because it
    /// changed while this call was committing; nothing was committed.</summary>
    NotActive = 4,

    /// <summary>The series end date is before today in its time zone.</summary>
    Ended = 5,

    /// <summary>A concurrent writer changed the series or inserted the same occurrences first and
    /// the series is still behind the target. Nothing was committed; the next pass resumes.</summary>
    Conflict = 6
}

/// <summary>What a materialization call did. <see cref="Inserted"/> counts only rows committed by
/// this call.</summary>
public sealed record EventSeriesMaterializationResult(
    EventSeriesMaterializationOutcome Outcome,
    int Inserted,
    DateOnly? CheckpointBefore,
    DateOnly? CheckpointAfter)
{
    public static EventSeriesMaterializationResult Nothing(EventSeriesMaterializationOutcome outcome, DateOnly? checkpoint = null) =>
        new(outcome, 0, checkpoint, checkpoint);
}
