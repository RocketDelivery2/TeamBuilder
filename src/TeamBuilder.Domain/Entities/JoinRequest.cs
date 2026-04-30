using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Domain.Entities;

public class JoinRequest : BaseEntity
{
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;
    public Guid PlayerId { get; set; }
    public Player Player { get; set; } = null!;
    public RequestStatus Status { get; set; }
    public string? Message { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
    public Guid? ProcessedByUserId { get; set; }
}
