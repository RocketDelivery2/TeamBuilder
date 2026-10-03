using System.ComponentModel.DataAnnotations;

namespace TeamBuilder.Application.DTOs;

public class RosterImportDto
{
    public Guid Id { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string RawData { get; set; } = string.Empty;
    public bool IsProcessed { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
    public string? ProcessingNotes { get; set; }
    public Guid? ImportedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// Listing view of a roster import, returned by the importer-scoped collection endpoint.
/// Omits <see cref="RosterImportDto.RawData"/>, <see cref="RosterImportDto.ProcessingNotes"/> and the
/// importer ID: the list is always the caller's own imports, and the full detail is importer-only.
/// </summary>
public class RosterImportSummaryDto
{
    public Guid Id { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public bool IsProcessed { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class CreateRosterImportDto
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string SourceName { get; set; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string SourceType { get; set; } = string.Empty;

    [Required]
    [MinLength(1)]
    public string RawData { get; set; } = string.Empty;
}
