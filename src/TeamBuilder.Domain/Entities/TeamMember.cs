using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Domain.Entities;

public class TeamMember : BaseEntity
{
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;
    public Guid PlayerId { get; set; }
    public Player Player { get; set; } = null!;
    public TeamRole Role { get; set; }
    public DateTime JoinedAtUtc { get; set; }
    public bool IsActive { get; set; }
}
