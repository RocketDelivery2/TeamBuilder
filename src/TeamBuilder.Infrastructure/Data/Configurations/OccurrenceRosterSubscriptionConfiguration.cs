using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Infrastructure.Data.Configurations;

/// <summary>
/// "Notify me" subscriptions. One per (player, occurrence, requirement). The requirement is
/// referenced through the composite (Id, OccurrenceId) key, so a subscription can never pair a
/// requirement with another occurrence; it cascades with the requirement (and so with a
/// deleted occurrence) and with a deleted player.
/// </summary>
public class OccurrenceRosterSubscriptionConfiguration : IEntityTypeConfiguration<OccurrenceRosterSubscription>
{
    public const string TableName = "OccurrenceRosterSubscriptions";
    public const string PlayerOccurrenceRequirementUniqueIndexName = "UX_OccurrenceRosterSubscriptions_PlayerId_OccurrenceId_RosterRequirementId";
    public const string RequirementOccurrenceIndexName = "IX_OccurrenceRosterSubscriptions_RosterRequirementId_OccurrenceId";
    public const string RequirementForeignKeyName = "FK_OccurrenceRosterSubscriptions_RosterRequirements_RosterRequirementId_OccurrenceId";
    public const string PlayerForeignKeyName = "FK_OccurrenceRosterSubscriptions_Players_PlayerId";

    public void Configure(EntityTypeBuilder<OccurrenceRosterSubscription> builder)
    {
        builder.ToTable(TableName);
        builder.HasKey(s => s.Id);

        builder.Property(s => s.CreatedAtUtc).IsRequired();
        builder.Property(s => s.RowVersion).IsRowVersion();

        builder.HasOne(s => s.Player)
            .WithMany()
            .HasForeignKey(s => s.PlayerId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName(PlayerForeignKeyName);

        builder.HasOne(s => s.RosterRequirement)
            .WithMany()
            .HasForeignKey(s => new { s.RosterRequirementId, s.OccurrenceId })
            .HasPrincipalKey(r => new { r.Id, r.OccurrenceId })
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName(RequirementForeignKeyName);

        // Idempotent subscribe, and the caller's own lookups.
        builder.HasIndex(s => new { s.PlayerId, s.OccurrenceId, s.RosterRequirementId })
            .IsUnique()
            .HasDatabaseName(PlayerOccurrenceRequirementUniqueIndexName);

        // The worker's fan-out (every subscriber of one requirement), in the composite foreign
        // key's column order so it also serves the FK.
        builder.HasIndex(s => new { s.RosterRequirementId, s.OccurrenceId })
            .HasDatabaseName(RequirementOccurrenceIndexName)
            .IncludeProperties(s => s.PlayerId);
    }
}
