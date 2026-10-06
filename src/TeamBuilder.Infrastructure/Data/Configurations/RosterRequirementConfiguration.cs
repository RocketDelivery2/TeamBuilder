using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TeamBuilder.Domain;
using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Infrastructure.Data.Configurations;

public class RosterRequirementConfiguration : IEntityTypeConfiguration<RosterRequirement>
{
    public const string TableName = "RosterRequirements";
    public const string OccurrenceRoleUniqueIndexName = "UX_RosterRequirements_OccurrenceId_RoleCode";
    public const string RequiredCountRangeCheckName = "CK_RosterRequirements_RequiredCount_Range";
    public const string RoleCodeNotEmptyCheckName = "CK_RosterRequirements_RoleCode_NotEmpty";

    /// <summary>Same public range as event/series participant ceilings.</summary>
    public const int MinRequiredCount = 1;
    public const int MaxRequiredCount = 100000;

    public const int DisplayPositionMaxLength = 100;
    public const int SourceRoleLabelMaxLength = 200;

    public void Configure(EntityTypeBuilder<RosterRequirement> builder)
    {
        builder.ToTable(TableName, t =>
        {
            t.HasCheckConstraint(
                RequiredCountRangeCheckName,
                $"[RequiredCount] >= {MinRequiredCount} AND [RequiredCount] <= {MaxRequiredCount}");
            t.HasCheckConstraint(RoleCodeNotEmptyCheckName, "[RoleCode] <> N''");
        });

        builder.HasKey(r => r.Id);

        // Target of RosterAssignments' composite (RequirementId, OccurrenceId) foreign key, which
        // makes "an assignment's requirement belongs to the same occurrence" a database rule.
        builder.HasAlternateKey(r => new { r.Id, r.OccurrenceId });

        builder.Property(r => r.RoleCode)
            .IsRequired()
            .HasMaxLength(RosterRoleCodes.MaxLength);

        builder.Property(r => r.DisplayPosition)
            .HasMaxLength(DisplayPositionMaxLength);

        builder.Property(r => r.SourceRoleLabel)
            .HasMaxLength(SourceRoleLabelMaxLength);

        // No model default: an unset value must fail the CHECK.
        builder.Property(r => r.RequiredCount)
            .IsRequired();

        builder.Property(r => r.CreatedAtUtc)
            .IsRequired();

        builder.Property(r => r.RowVersion)
            .IsRowVersion();

        // Requirements belong to their occurrence and go with it.
        builder.HasOne(r => r.EventOccurrence)
            .WithMany(e => e.RosterRequirements)
            .HasForeignKey(r => r.OccurrenceId)
            .OnDelete(DeleteBehavior.Cascade);

        // One requirement per canonical role per occurrence; generic demand is "participant".
        // Role codes are stored normalized (lowercase), so the column collation does not matter.
        builder.HasIndex(r => new { r.OccurrenceId, r.RoleCode })
            .IsUnique()
            .HasDatabaseName(OccurrenceRoleUniqueIndexName);
    }
}
