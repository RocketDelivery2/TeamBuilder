using System.ComponentModel.DataAnnotations;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Application.DTOs;

public class EventDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime EventDateUtc { get; set; }
    public EventStatus Status { get; set; }
    public string? Category { get; set; }
    public string? Tags { get; set; }
    public string? Location { get; set; }
    public string? Region { get; set; }
    public int MaxParticipants { get; set; }
    public int CurrentParticipantCount { get; set; }
    public Guid? TeamId { get; set; }
    public string? TeamName { get; set; }
    public Guid? HostId { get; set; }
    public string? HostUsername { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class CreateEventDto
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    [Required]
    public DateTime? EventDateUtc { get; set; }

    [StringLength(100)]
    public string? Category { get; set; }

    [StringLength(500)]
    public string? Tags { get; set; }

    [StringLength(200)]
    public string? Location { get; set; }

    [StringLength(100)]
    public string? Region { get; set; }

    [Range(1, 100000)]
    public int MaxParticipants { get; set; } = 50;

    public Guid? TeamId { get; set; }
}

public class UpdateEventDto
{
    [StringLength(200, MinimumLength = 1)]
    public string? Name { get; set; }

    [StringLength(2000)]
    public string? Description { get; set; }

    public DateTime? EventDateUtc { get; set; }

    [EnumDataType(typeof(EventStatus))]
    public EventStatus? Status { get; set; }

    [StringLength(100)]
    public string? Category { get; set; }

    [StringLength(500)]
    public string? Tags { get; set; }

    [StringLength(200)]
    public string? Location { get; set; }

    [StringLength(100)]
    public string? Region { get; set; }

    [Range(1, 100000)]
    public int? MaxParticipants { get; set; }
}
