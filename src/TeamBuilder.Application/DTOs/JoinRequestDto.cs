using System.ComponentModel.DataAnnotations;
using TeamBuilder.Application.Validation;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Application.DTOs;

public class JoinRequestDto
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public string? TeamName { get; set; }
    public Guid PlayerId { get; set; }
    public string? PlayerUsername { get; set; }
    public RequestStatus Status { get; set; }
    public string? Message { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
}

public class CreateJoinRequestDto
{
    [Required]
    [NonEmptyGuid]
    public Guid? TeamId { get; set; }

    [StringLength(1000)]
    public string? Message { get; set; }
}

public class ProcessJoinRequestDto
{
    [Required]
    [EnumDataType(typeof(RequestStatus))]
    public RequestStatus Status { get; set; }
}
