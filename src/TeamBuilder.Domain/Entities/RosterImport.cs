namespace TeamBuilder.Domain.Entities;

public class RosterImport : BaseEntity
{
    public string SourceName { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string RawData { get; set; } = string.Empty;
    public bool IsProcessed { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
    public string? ProcessingNotes { get; set; }
    public Guid? ImportedByUserId { get; set; }
}
