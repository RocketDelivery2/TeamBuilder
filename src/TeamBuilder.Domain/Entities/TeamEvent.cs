using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Domain.Entities;

public class TeamEvent : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime EventDateUtc { get; set; }
    public EventStatus Status { get; set; }
    public string? Category { get; set; }
    public string? Tags { get; set; }
    public string? Location { get; set; }
    public string? Region { get; set; }
    public int MaxParticipants { get; set; }
    public int CurrentParticipantCount { get; set; }
    public Guid? TeamId { get; set; }
    public Team? Team { get; set; }
    public Guid? HostId { get; set; }
    public Player? Host { get; set; }
    public ICollection<RosterEntry> RosterEntries { get; set; } = new List<RosterEntry>();
}
