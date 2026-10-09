namespace TeamBuilder.Domain.Entities;

/// <summary>
/// A persistent in-app notification for one player. The structured identifiers are the
/// authority (what to open, which requirement); <see cref="Title"/> and <see cref="Body"/> are
/// presentation only and never name the player whose departure caused it.
/// (<see cref="SourceEventId"/>, <see cref="PlayerId"/>) is unique, which makes creating it
/// idempotent when the outbox hands the same event over more than once.
/// </summary>
public class InAppNotification : BaseEntity
{
    public Guid PlayerId { get; set; }
    public Player Player { get; set; } = null!;

    /// <summary>Stable type string, e.g. <c>roster.vacancy</c>.</summary>
    public string Type { get; set; } = string.Empty;

    public Guid OccurrenceId { get; set; }
    public Guid? RosterRequirementId { get; set; }

    /// <summary>The outbox event that produced it.</summary>
    public Guid SourceEventId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    public DateTime? ReadAtUtc { get; set; }
}
