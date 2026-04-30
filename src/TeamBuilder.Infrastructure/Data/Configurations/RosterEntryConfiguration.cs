using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Infrastructure.Data.Configurations;

public class RosterEntryConfiguration : IEntityTypeConfiguration<RosterEntry>
{
    public void Configure(EntityTypeBuilder<RosterEntry> builder)
    {
        builder.HasKey(re => re.Id);

        builder.Property(re => re.Position)
            .HasMaxLength(100);

        builder.Property(re => re.Notes)
            .HasMaxLength(500);

        builder.Property(re => re.RegisteredAtUtc)
            .IsRequired();

        builder.Property(re => re.CreatedAtUtc)
            .IsRequired();

        builder.Property(re => re.RowVersion)
            .IsRowVersion();

        builder.HasIndex(re => new { re.EventId, re.PlayerId });
        builder.HasIndex(re => re.IsConfirmed);
    }
}
