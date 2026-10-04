namespace TeamBuilder.Application.DTOs;

public class PublicPlayerDto
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Bio { get; set; }
    public string? Region { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}
