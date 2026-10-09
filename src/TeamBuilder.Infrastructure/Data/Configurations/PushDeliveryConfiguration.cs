using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Infrastructure.Data.Configurations;

/// <summary>
/// The push delivery ledger. (InAppNotificationId, PushSubscriptionId) is unique, so a
/// notification is queued at most once per device. It cascades with its notification; the
/// subscription is referenced without a foreign key (see <see cref="PushDelivery"/>). Filtered
/// indexes serve the dispatcher's two claim predicates and retention, as for the outbox.
/// </summary>
public class PushDeliveryConfiguration : IEntityTypeConfiguration<PushDelivery>
{
    public const string TableName = "PushDeliveries";
    public const string NotificationSubscriptionUniqueIndexName = "UX_PushDeliveries_InAppNotificationId_PushSubscriptionId";
    public const string PendingIndexName = "IX_PushDeliveries_Pending_NextAttemptAtUtc";
    public const string SendingIndexName = "IX_PushDeliveries_Sending_LockExpiresAtUtc";
    public const string CompletedIndexName = "IX_PushDeliveries_Terminal_CompletedAtUtc";
    public const string NotificationForeignKeyName = "FK_PushDeliveries_InAppNotifications_InAppNotificationId";
    public const string StatusRangeCheckName = "CK_PushDeliveries_Status_Range";

    public const int LockOwnerMaxLength = 200;
    public const int LastErrorMaxLength = 100;

    public static readonly string PendingFilter = $"[Status] = {(int)PushDeliveryStatus.Pending}";
    public static readonly string SendingFilter = $"[Status] = {(int)PushDeliveryStatus.Sending}";
    public static readonly string TerminalFilter = $"[Status] >= {(int)PushDeliveryStatus.Accepted}";

    public void Configure(EntityTypeBuilder<PushDelivery> builder)
    {
        builder.ToTable(TableName, t =>
        {
            var statuses = Enum.GetValues<PushDeliveryStatus>().Cast<int>().Order().ToList();
            t.HasCheckConstraint(StatusRangeCheckName, $"[Status] >= {statuses[0]} AND [Status] <= {statuses[^1]}");
        });

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.Status).IsRequired();
        builder.Property(d => d.LockOwner).HasMaxLength(LockOwnerMaxLength).IsUnicode(false);
        builder.Property(d => d.LastError).HasMaxLength(LastErrorMaxLength).IsUnicode(false);

        builder.HasOne<InAppNotification>()
            .WithMany()
            .HasForeignKey(d => d.InAppNotificationId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName(NotificationForeignKeyName);

        builder.HasIndex(d => new { d.InAppNotificationId, d.PushSubscriptionId })
            .IsUnique()
            .HasDatabaseName(NotificationSubscriptionUniqueIndexName);

        builder.HasIndex(d => new { d.NextAttemptAtUtc, d.CreatedAtUtc })
            .HasDatabaseName(PendingIndexName)
            .HasFilter(PendingFilter)
            .IncludeProperties(d => d.AttemptCount);

        builder.HasIndex(d => d.LockExpiresAtUtc)
            .HasDatabaseName(SendingIndexName)
            .HasFilter(SendingFilter)
            .IncludeProperties(d => d.AttemptCount);

        builder.HasIndex(d => d.CompletedAtUtc)
            .HasDatabaseName(CompletedIndexName)
            .HasFilter(TerminalFilter);
    }
}
