namespace TeamBuilder.Domain.Entities;

/// <summary>
/// Imported/raw roster association and provenance (roster-import staging): "the source listed
/// this player on this event". It is NOT live TeamBuilder participation and must not be
/// repurposed as such; live participation state is <see cref="RosterAssignment"/>.
/// </summary>
public class RosterEntry : BaseEntity
{
    public Guid EventId { get; set; }
    public EventOccurrence Event { get; set; } = null!;
    public Guid PlayerId { get; set; }
    public Player Player { get; set; } = null!;
    public string? Position { get; set; }
    public string? Notes { get; set; }
    public bool IsConfirmed { get; set; }
    public DateTime RegisteredAtUtc { get; set; }
}
