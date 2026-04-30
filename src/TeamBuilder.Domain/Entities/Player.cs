namespace TeamBuilder.Domain.Entities;

public class Player : BaseEntity
{
    public string Username { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string? Bio { get; set; }
    public string? Region { get; set; }
    public string? AvatarUrl { get; set; }
    public ICollection<TeamMember> TeamMemberships { get; set; } = new List<TeamMember>();
    public ICollection<Team> OwnedTeams { get; set; } = new List<Team>();
    public ICollection<TeamEvent> HostedEvents { get; set; } = new List<TeamEvent>();
    public ICollection<RosterEntry> RosterEntries { get; set; } = new List<RosterEntry>();
    public ICollection<JoinRequest> JoinRequests { get; set; } = new List<JoinRequest>();
}
