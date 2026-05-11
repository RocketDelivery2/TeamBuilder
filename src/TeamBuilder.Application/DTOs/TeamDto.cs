using System.ComponentModel.DataAnnotations;
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
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    [Range(1, 1000)]
    public int MaxMembers { get; set; } = 10;

    [StringLength(100)]
    public string? Region { get; set; }

    [StringLength(100)]
    public string? Category { get; set; }

    [StringLength(500)]
    public string? Tags { get; set; }
}

public class UpdateTeamDto
{
    [StringLength(200, MinimumLength = 1)]
    public string? Name { get; set; }

    [StringLength(2000)]
    public string? Description { get; set; }

    public TeamStatus? Status { get; set; }

    [Range(1, 1000)]
    public int? MaxMembers { get; set; }

    [StringLength(100)]
    public string? Region { get; set; }

    [StringLength(100)]
    public string? Category { get; set; }

    [StringLength(500)]
    public string? Tags { get; set; }
}
