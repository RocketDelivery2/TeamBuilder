namespace TeamBuilder.Domain.Entities;

public class Player : BaseEntity
{
    public string Username { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string? Bio { get; set; }
    public string? Region { get; set; }
    public string? AvatarUrl { get; set; }
    public ICollection<TeamMember> TeamMemberships { get; set; } = [];
    public ICollection<Team> OwnedTeams { get; set; } = [];
    public ICollection<EventOccurrence> HostedEvents { get; set; } = [];
    public ICollection<RosterEntry> RosterEntries { get; set; } = [];
    public ICollection<RosterAssignment> RosterAssignments { get; set; } = [];
    public ICollection<JoinRequest> JoinRequests { get; set; } = [];
    public ICollection<PlayerIdentity> Identities { get; set; } = [];
}