using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Application.DTOs;

public class TeamDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TeamStatus Status { get; set; }
    public int MaxMembers { get; set; }
    public int CurrentMemberCount { get; set; }
    public string? Region { get; set; }
    public string? Category { get; set; }
    public string? Tags { get; set; }
    public Guid? OwnerId { get; set; }
    public string? OwnerUsername { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class CreateTeamDto
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int MaxMembers { get; set; } = 10;
    public string? Region { get; set; }
    public string? Category { get; set; }
    public string? Tags { get; set; }
}

public class UpdateTeamDto
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public TeamStatus? Status { get; set; }
    public int? MaxMembers { get; set; }
    public string? Region { get; set; }
    public string? Category { get; set; }
    public string? Tags { get; set; }
}
