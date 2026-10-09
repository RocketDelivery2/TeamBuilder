using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Infrastructure.Data.Configurations;

/// <summary>
/// The transactional outbox. No foreign keys: a message is an immutable fact about a moment
/// and must outlive whatever it refers to. Indexes serve the worker's two claim predicates
/// (filtered to the only statuses it ever claims, so completed history never slows them) and
/// the deduplication guarantee.
/// </summary>
public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public const string TableName = "OutboxMessages";
    public const string DeduplicationKeyUniqueIndexName = "UX_OutboxMessages_DeduplicationKey";
    public const string PendingIndexName = "IX_OutboxMessages_Pending_NextAttemptAtUtc";
    public const string ProcessingIndexName = "IX_OutboxMessages_Processing_LockExpiresAtUtc";
    public const string CompletedIndexName = "IX_OutboxMessages_Completed_ProcessedAtUtc";
    public const string FailedIndexName = "IX_OutboxMessages_Failed_ProcessedAtUtc";
    public const string StatusRangeCheckName = "CK_OutboxMessages_Status_Range";
    public const string AttemptCountCheckName = "CK_OutboxMessages_AttemptCount_NonNegative";
    public const string ReplayCountCheckName = "CK_OutboxMessages_ReplayCounts_NonNegative";

    public const int TypeMaxLength = 200;
    public const int DeduplicationKeyMaxLength = 200;
    public const int LockOwnerMaxLength = 200;
    public const int LastErrorMaxLength = 2000;

    public static readonly string PendingFilter = $"[Status] = {(int)OutboxMessageStatus.Pending}";
    public static readonly string ProcessingFilter = $"[Status] = {(int)OutboxMessageStatus.Processing}";
    public static readonly string CompletedFilter = $"[Status] = {(int)OutboxMessageStatus.Completed}";
    public static readonly string FailedFilter = $"[Status] = {(int)OutboxMessageStatus.Failed}";

    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable(TableName, t =>
        {
            var statuses = Enum.GetValues<OutboxMessageStatus>().Cast<int>().Order().ToList();
            t.HasCheckConstraint(StatusRangeCheckName, $"[Status] >= {statuses[0]} AND [Status] <= {statuses[^1]}");
            t.HasCheckConstraint(AttemptCountCheckName, "[AttemptCount] >= 0");
            t.HasCheckConstraint(ReplayCountCheckName, "[ReplayCount] >= 0 AND [PriorAttemptCount] >= 0");
        });

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.Type).IsRequired().HasMaxLength(TypeMaxLength).IsUnicode(false);
        builder.Property(m => m.PayloadJson).IsRequired();
        builder.Property(m => m.DeduplicationKey).IsRequired().HasMaxLength(DeduplicationKeyMaxLength).IsUnicode(false);
        builder.Property(m => m.Status).IsRequired();
        builder.Property(m => m.LockOwner).HasMaxLength(LockOwnerMaxLength).IsUnicode(false);
        builder.Property(m => m.LastError).HasMaxLength(LastErrorMaxLength);

        builder.HasIndex(m => m.DeduplicationKey)
            .IsUnique()
            .HasDatabaseName(DeduplicationKeyUniqueIndexName);

        // "Pending and due": seek on NextAttemptAtUtc among Pending rows only.
        builder.HasIndex(m => new { m.NextAttemptAtUtc, m.CreatedAtUtc })
            .HasDatabaseName(PendingIndexName)
            .HasFilter(PendingFilter)
            .IncludeProperties(m => m.AttemptCount);

        // "Processing with an expired lease": seek on LockExpiresAtUtc among Processing rows only.
        builder.HasIndex(m => m.LockExpiresAtUtc)
            .HasDatabaseName(ProcessingIndexName)
            .HasFilter(ProcessingFilter)
            .IncludeProperties(m => m.AttemptCount);

        // Retention: "Completed and processed before the cutoff", oldest first, in small batches.
        builder.HasIndex(m => m.ProcessedAtUtc, CompletedIndexName)
            .HasFilter(CompletedFilter);

        // Operator inspection of Failed messages, newest first, without scanning history.
        builder.HasIndex(m => m.ProcessedAtUtc, FailedIndexName)
            .HasFilter(FailedFilter)
            .IncludeProperties(m => new { m.Type, m.AggregateId, m.AttemptCount, m.ReplayCount });
    }
}
