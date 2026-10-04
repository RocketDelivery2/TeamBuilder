using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Infrastructure.Data.Configurations;

public class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public const string CurrentMemberCountBoundsCheckName = "CK_Teams_CurrentMemberCount_Bounds";

    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.HasKey(t => t.Id);

        // Physical capacity bounds only. Exact equality with the active TeamMember rows is
        // the application's job (see the reconciliation in TeamService/JoinRequestService/
        // PlayerService); this check just makes an out-of-range stored count impossible.
        builder.ToTable(t => t.HasCheckConstraint(
            CurrentMemberCountBoundsCheckName,
            "[CurrentMemberCount] >= 0 AND [CurrentMemberCount] <= [MaxMembers]"));

        builder.Property(t => t.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(t => t.Description)
            .HasMaxLength(2000);

        builder.Property(t => t.Status)
            .IsRequired();

        builder.Property(t => t.Region)
            .HasMaxLength(100);

        builder.Property(t => t.Category)
            .HasMaxLength(100);

        builder.Property(t => t.Tags)
            .HasMaxLength(500);

        builder.Property(t => t.CreatedAtUtc)
            .IsRequired();

        builder.Property(t => t.RowVersion)
            .IsRowVersion();

        // The database refuses to delete a Player who still owns a Team (NO ACTION on SQL
        // Server). PlayerService.DeleteAsync returns a friendly 409 first; this FK is the
        // race-safe final guard so a Team can never be silently left ownerless.
        builder.HasOne(t => t.Owner)
            .WithMany(p => p.OwnedTeams)
            .HasForeignKey(t => t.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(t => t.Members)
            .WithOne(tm => tm.Team)
            .HasForeignKey(tm => tm.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(t => t.Events)
            .WithOne(e => e.Team)
            .HasForeignKey(e => e.TeamId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(t => t.JoinRequests)
            .WithOne(jr => jr.Team)
            .HasForeignKey(jr => jr.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(t => t.Status);
        builder.HasIndex(t => t.Category);
        builder.HasIndex(t => t.Region);
        builder.HasIndex(t => t.CreatedAtUtc);
    }
}
