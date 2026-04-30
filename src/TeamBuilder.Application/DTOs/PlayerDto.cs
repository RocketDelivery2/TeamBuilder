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
    public string Username { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string? Bio { get; set; }
    public string? Region { get; set; }
    public string? AvatarUrl { get; set; }
}

public class UpdatePlayerDto
{
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string? Bio { get; set; }
    public string? Region { get; set; }
    public string? AvatarUrl { get; set; }
}
