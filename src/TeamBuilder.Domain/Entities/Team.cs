using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Domain.Entities;

public class Team : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TeamLifecycleStatus LifecycleStatus { get; set; } = TeamLifecycleStatus.Active;

    /// <summary>
    /// Whether the owner is accepting new join requests. Always false unless
    /// <see cref="LifecycleStatus"/> is Active.
    /// </summary>
    public bool IsAcceptingMembers { get; set; } = true;
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

    // Derived, never persisted.
    public bool IsFull => TeamState.IsFull(CurrentMemberCount, MaxMembers);
    public int OpenSlots => TeamState.OpenSlots(CurrentMemberCount, MaxMembers);
    public bool HasVacancies => TeamState.HasVacancies(LifecycleStatus, IsAcceptingMembers, CurrentMemberCount, MaxMembers);
    public TeamStatus LegacyStatus => TeamState.ToLegacyStatus(LifecycleStatus, IsAcceptingMembers, CurrentMemberCount, MaxMembers);
}
