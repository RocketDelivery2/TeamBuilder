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

public class CreateRosterImportDto
{
    public string SourceName { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string RawData { get; set; } = string.Empty;
}
