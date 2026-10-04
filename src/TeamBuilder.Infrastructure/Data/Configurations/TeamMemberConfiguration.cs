using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Infrastructure.Data.Configurations;

public class TeamMemberConfiguration : IEntityTypeConfiguration<TeamMember>
{
    public void Configure(EntityTypeBuilder<TeamMember> builder)
    {
        builder.HasKey(tm => tm.Id);

        builder.Property(tm => tm.Role)
            .IsRequired();

        builder.Property(tm => tm.JoinedAtUtc)
            .IsRequired();

        builder.Property(tm => tm.CreatedAtUtc)
            .IsRequired();

        builder.Property(tm => tm.RowVersion)
            .IsRowVersion();

        // A player can only hold one active membership per team at a time.
        // Filtered on IsActive so a player who has left a team (soft-deleted,
        // see TeamService.RemoveMemberAsync) can still rejoin later without
        // colliding with their own historical inactive membership row.
        builder.HasIndex(tm => new { tm.TeamId, tm.PlayerId })
            .IsUnique()
            .HasFilter("[IsActive] = 1")
            .HasDatabaseName("UX_TeamMembers_TeamId_PlayerId");

        // Capacity hot paths. Both are unfiltered so they also serve the FK checks and the
        // Team cascade (which must see inactive rows too); they replace the FK-convention
        // PlayerId index and the low-selectivity standalone IsActive index.
        // TeamId + IsActive: active/total roster counts and Team delete cascade.
        builder.HasIndex(tm => new { tm.TeamId, tm.IsActive })
            .HasDatabaseName("IX_TeamMembers_TeamId_IsActive");

        // PlayerId + IsActive: player deletion (affected teams, explicit membership delete,
        // and the NO ACTION FK check on the Player delete itself).
        builder.HasIndex(tm => new { tm.PlayerId, tm.IsActive })
            .HasDatabaseName("IX_TeamMembers_PlayerId_IsActive");
    }
}
