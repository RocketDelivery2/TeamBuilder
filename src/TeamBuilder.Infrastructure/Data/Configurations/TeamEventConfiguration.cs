using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Infrastructure.Data.Configurations;

public class TeamEventConfiguration : IEntityTypeConfiguration<TeamEvent>
{
    public void Configure(EntityTypeBuilder<TeamEvent> builder)
    {
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(e => e.Description)
            .HasMaxLength(2000);

        builder.Property(e => e.EventDateUtc)
            .IsRequired();

        builder.Property(e => e.Status)
            .IsRequired();

        builder.Property(e => e.Category)
            .HasMaxLength(100);

        builder.Property(e => e.Tags)
            .HasMaxLength(500);

        builder.Property(e => e.Location)
            .HasMaxLength(200);

        builder.Property(e => e.Region)
            .HasMaxLength(100);

        builder.Property(e => e.CreatedAtUtc)
            .IsRequired();

        builder.Property(e => e.RowVersion)
            .IsRowVersion();

        builder.HasMany(e => e.RosterEntries)
            .WithOne(re => re.Event)
            .HasForeignKey(re => re.EventId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.EventDateUtc);
        builder.HasIndex(e => e.Status);
        builder.HasIndex(e => e.Category);
        builder.HasIndex(e => e.Region);
        builder.HasIndex(e => e.CreatedAtUtc);
    }
}
