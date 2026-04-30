namespace TeamBuilder.Domain.Entities;

public class RosterEntry : BaseEntity
{
    public Guid EventId { get; set; }
    public TeamEvent Event { get; set; } = null!;
    public Guid PlayerId { get; set; }
    public Player Player { get; set; } = null!;
    public string? Position { get; set; }
    public string? Notes { get; set; }
    public bool IsConfirmed { get; set; }
    public DateTime RegisteredAtUtc { get; set; }
}
