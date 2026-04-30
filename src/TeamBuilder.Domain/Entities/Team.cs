using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Domain.Entities;

public class Team : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TeamStatus Status { get; set; }
    public int MaxMembers { get; set; }
    public int CurrentMemberCount { get; set; }
    public string? Region { get; set; }
    public string? Category { get; set; }
    public string? Tags { get; set; }
    public Guid? OwnerId { get; set; }
    public Player? Owner { get; set; }
    public ICollection<TeamMember> Members { get; set; } = new List<TeamMember>();
    public ICollection<TeamEvent> Events { get; set; } = new List<TeamEvent>();
    public ICollection<JoinRequest> JoinRequests { get; set; } = new List<JoinRequest>();
}
