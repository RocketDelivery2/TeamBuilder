using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TeamBuilder.Domain;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Infrastructure.Data.Configurations;

/// <summary>
/// Live participation rows. Database invariants:
/// <list type="bullet">
/// <item>one supply assignment (Reserved/Confirmed/CheckedIn/Active) per player per occurrence
/// (filtered unique index); historical rows are unrestricted;</item>
/// <item>a requirement, when referenced, belongs to the same occurrence (composite FK);</item>
/// <item>a replaced assignment belongs to the same occurrence (composite FK) and is never the
/// row itself (CHECK). Longer cycles are not detected.</item>
/// </list>
/// <item>an occurrence with assignments cannot be deleted (NO ACTION), so history survives.</item>
/// There is deliberately no relationship to TeamMember.
/// </summary>
public class RosterAssignmentConfiguration : IEntityTypeConfiguration<RosterAssignment>
{
    public const string TableName = "RosterAssignments";
    public const string SupplyUniqueIndexName = "UX_RosterAssignments_OccurrenceId_PlayerId_Supply";
    public const string RequirementForeignKeyName = "FK_RosterAssignments_RosterRequirements_RequirementId_OccurrenceId";
    public const string ReplacedAssignmentForeignKeyName = "FK_RosterAssignments_RosterAssignments_ReplacedAssignmentId_OccurrenceId";
    public const string PlayerForeignKeyName = "FK_RosterAssignments_Players_PlayerId";
    public const string OccurrenceForeignKeyName = "FK_RosterAssignments_Events_OccurrenceId";
    public const string NotSelfReplacementCheckName = "CK_RosterAssignments_ReplacedAssignmentId_NotSelf";
    public const string StatusRangeCheckName = "CK_RosterAssignments_Status_Range";
    public const string SourceRangeCheckName = "CK_RosterAssignments_Source_Range";
    public const string ExitReasonRangeCheckName = "CK_RosterAssignments_ExitReason_Range";

    public const int SourceRoleLabelMaxLength = RosterRequirementConfiguration.SourceRoleLabelMaxLength;

    /// <summary>
    /// SQL filter selecting supply statuses, built from <see cref="RosterState.SupplyStatuses"/>
    /// so the index and the in-memory rule cannot drift: <c>[Status] IN (1, 2, 3, 4)</c>.
    /// </summary>
    public static readonly string SupplyStatusFilter =
        $"[Status] IN ({string.Join(", ", RosterState.SupplyStatuses.Select(s => (int)s))})";

    public void Configure(EntityTypeBuilder<RosterAssignment> builder)
    {
        builder.ToTable(TableName, t =>
        {
            t.HasCheckConstraint(
                NotSelfReplacementCheckName,
                "[ReplacedAssignmentId] IS NULL OR [ReplacedAssignmentId] <> [Id]");
            t.HasCheckConstraint(StatusRangeCheckName, RangeCheck("Status", Enum.GetValues<RosterAssignmentStatus>().Cast<int>()));
            t.HasCheckConstraint(SourceRangeCheckName, RangeCheck("Source", Enum.GetValues<RosterAssignmentSource>().Cast<int>()));
            t.HasCheckConstraint(
                ExitReasonRangeCheckName,
                $"[ExitReason] IS NULL OR ({RangeCheck("ExitReason", Enum.GetValues<RosterExitReason>().Cast<int>())})");
        });

        builder.HasKey(a => a.Id);

        // Target of the replacement composite FK below.
        builder.HasAlternateKey(a => new { a.Id, a.OccurrenceId });

        builder.Property(a => a.RoleCode)
            .HasMaxLength(RosterRoleCodes.MaxLength);

        builder.Property(a => a.SourceRoleLabel)
            .HasMaxLength(SourceRoleLabelMaxLength);

        builder.Property(a => a.Status)
            .IsRequired();

        builder.Property(a => a.Source)
            .IsRequired();

        builder.Property(a => a.CreatedAtUtc)
            .IsRequired();

        builder.Property(a => a.RowVersion)
            .IsRowVersion();

        // Participation history is never silently erased by deleting its occurrence (NO ACTION):
        // EventService.DeleteAsync returns 409 OccurrenceHasParticipationHistory first, and this
        // FK is the race-safe final guard. Cancel an occurrence instead of deleting it.
        builder.HasOne(a => a.EventOccurrence)
            .WithMany(e => e.RosterAssignments)
            .HasForeignKey(a => a.OccurrenceId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(OccurrenceForeignKeyName);

        // Participation history is never silently erased by deleting a player (NO ACTION).
        builder.HasOne(a => a.Player)
            .WithMany(p => p.RosterAssignments)
            .HasForeignKey(a => a.PlayerId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(PlayerForeignKeyName);

        // Composite FK: a non-null RequirementId must name a requirement of the SAME occurrence.
        // SQL Server does not check a composite FK while any of its columns is NULL, so a null
        // RequirementId stays allowed. NO ACTION: a requirement in use cannot be deleted.
        builder.HasOne(a => a.Requirement)
            .WithMany()
            .HasForeignKey(a => new { a.RequirementId, a.OccurrenceId })
            .HasPrincipalKey(r => new { r.Id, r.OccurrenceId })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName(RequirementForeignKeyName);

        // Composite self-FK: a replacement points at an earlier assignment of the same occurrence.
        builder.HasOne(a => a.ReplacedAssignment)
            .WithMany()
            .HasForeignKey(a => new { a.ReplacedAssignmentId, a.OccurrenceId })
            .HasPrincipalKey(a => new { a.Id, a.OccurrenceId })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName(ReplacedAssignmentForeignKeyName);

        // At most one supply assignment per player per occurrence; Departed/NoShow/Cancelled
        // history is unrestricted.
        builder.HasIndex(a => new { a.OccurrenceId, a.PlayerId })
            .IsUnique()
            .HasDatabaseName(SupplyUniqueIndexName)
            .HasFilter(SupplyStatusFilter);

        builder.HasIndex(a => a.PlayerId);
        builder.HasIndex(a => new { a.OccurrenceId, a.Status });

        // The requirement FK index, made covering for discovery's openOnly predicate ("some
        // requirement has fewer current assignments than it requires"), which counts supply
        // rows per requirement for every candidate game; without Status every counted row cost
        // a clustered key lookup.
        builder.HasIndex(a => new { a.RequirementId, a.OccurrenceId })
            .IncludeProperties(a => a.Status);
    }

    private static string RangeCheck(string column, IEnumerable<int> values)
    {
        var ordered = values.Order().ToList();
        return $"[{column}] >= {ordered[0]} AND [{column}] <= {ordered[^1]}";
    }
}
