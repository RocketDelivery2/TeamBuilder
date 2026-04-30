using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Infrastructure.Data.Configurations;

public class RosterImportConfiguration : IEntityTypeConfiguration<RosterImport>
{
    public void Configure(EntityTypeBuilder<RosterImport> builder)
    {
        builder.HasKey(ri => ri.Id);

        builder.Property(ri => ri.SourceName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(ri => ri.SourceType)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(ri => ri.RawData)
            .IsRequired();

        builder.Property(ri => ri.ProcessingNotes)
            .HasMaxLength(2000);

        builder.Property(ri => ri.CreatedAtUtc)
            .IsRequired();

        builder.Property(ri => ri.RowVersion)
            .IsRowVersion();

        builder.HasIndex(ri => ri.IsProcessed);
        builder.HasIndex(ri => ri.CreatedAtUtc);
    }
}
