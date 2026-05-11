using System.ComponentModel.DataAnnotations;

namespace TeamBuilder.Application.DTOs;

public class PlayerDto
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string? Bio { get; set; }
    public string? Region { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class CreatePlayerDto
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Username { get; set; } = string.Empty;

    [EmailAddress]
    [StringLength(256)]
    public string? Email { get; set; }

    [StringLength(200)]
    public string? DisplayName { get; set; }

    [StringLength(1000)]
    public string? Bio { get; set; }

    [StringLength(100)]
    public string? Region { get; set; }

    [StringLength(500)]
    public string? AvatarUrl { get; set; }
}

public class UpdatePlayerDto
{
    [EmailAddress]
    [StringLength(256)]
    public string? Email { get; set; }

    [StringLength(200)]
    public string? DisplayName { get; set; }

    [StringLength(1000)]
    public string? Bio { get; set; }

    [StringLength(100)]
    public string? Region { get; set; }

    [StringLength(500)]
    public string? AvatarUrl { get; set; }
}
