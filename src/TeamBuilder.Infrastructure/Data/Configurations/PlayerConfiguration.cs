using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Infrastructure.Data.Configurations;

public class PlayerConfiguration : IEntityTypeConfiguration<Player>
{
    public void Configure(EntityTypeBuilder<Player> builder)
    {
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Username)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(p => p.Email)
            .HasMaxLength(256);

        builder.Property(p => p.DisplayName)
            .HasMaxLength(200);

        builder.Property(p => p.Bio)
            .HasMaxLength(1000);

        builder.Property(p => p.Region)
            .HasMaxLength(100);

        builder.Property(p => p.AvatarUrl)
            .HasMaxLength(500);

        builder.Property(p => p.CreatedAtUtc)
            .IsRequired();

        builder.Property(p => p.RowVersion)
            .IsRowVersion();

        builder.HasMany(p => p.TeamMemberships)
            .WithOne(tm => tm.Player)
            .HasForeignKey(tm => tm.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(p => p.HostedEvents)
            .WithOne(e => e.Host)
            .HasForeignKey(e => e.HostId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(p => p.RosterEntries)
            .WithOne(re => re.Player)
            .HasForeignKey(re => re.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(p => p.JoinRequests)
            .WithOne(jr => jr.Player)
            .HasForeignKey(jr => jr.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(p => p.Username).IsUnique();
        builder.HasIndex(p => p.Email);
        builder.HasIndex(p => p.Region);
        builder.HasIndex(p => p.CreatedAtUtc);
    }
}
