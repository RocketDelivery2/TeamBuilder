using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Infrastructure.Data.Configurations;

/// <summary>
/// Persistent in-app notifications. (SourceEventId, PlayerId) is unique: the consumer-side
/// idempotency of at-least-once outbox processing. The occurrence foreign key is NO ACTION:
/// notifications exist only for occurrences with roster history, which can never be deleted.
/// </summary>
public class InAppNotificationConfiguration : IEntityTypeConfiguration<InAppNotification>
{
    public const string TableName = "InAppNotifications";
    public const string SourceEventPlayerUniqueIndexName = "UX_InAppNotifications_SourceEventId_PlayerId";
    public const string PlayerTimelineIndexName = "IX_InAppNotifications_PlayerId_CreatedAtUtc_Id";
    public const string PlayerUnreadIndexName = "IX_InAppNotifications_PlayerId_Unread";
    public const string PlayerForeignKeyName = "FK_InAppNotifications_Players_PlayerId";
    public const string OccurrenceForeignKeyName = "FK_InAppNotifications_Events_OccurrenceId";

    public const int TypeMaxLength = 100;
    public const int TitleMaxLength = 200;
    public const int BodyMaxLength = 1000;

    public void Configure(EntityTypeBuilder<InAppNotification> builder)
    {
        builder.ToTable(TableName);
        builder.HasKey(n => n.Id);

        builder.Property(n => n.Type).IsRequired().HasMaxLength(TypeMaxLength).IsUnicode(false);
        builder.Property(n => n.Title).IsRequired().HasMaxLength(TitleMaxLength);
        builder.Property(n => n.Body).IsRequired().HasMaxLength(BodyMaxLength);
        builder.Property(n => n.CreatedAtUtc).IsRequired();
        builder.Property(n => n.RowVersion).IsRowVersion();

        builder.HasOne(n => n.Player)
            .WithMany()
            .HasForeignKey(n => n.PlayerId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName(PlayerForeignKeyName);

        builder.HasOne<EventOccurrence>()
            .WithMany()
            .HasForeignKey(n => n.OccurrenceId)
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName(OccurrenceForeignKeyName);

        builder.HasIndex(n => new { n.SourceEventId, n.PlayerId })
            .IsUnique()
            .HasDatabaseName(SourceEventPlayerUniqueIndexName);

        // The caller's timeline, newest first, keyset on (CreatedAtUtc, Id).
        builder.HasIndex(n => new { n.PlayerId, n.CreatedAtUtc, n.Id }, PlayerTimelineIndexName)
            .IsDescending(false, true, true)
            .IncludeProperties(n => n.ReadAtUtc);

        // The unread badge and unreadOnly pages.
        builder.HasIndex(n => new { n.PlayerId, n.CreatedAtUtc, n.Id }, PlayerUnreadIndexName)
            .IsDescending(false, true, true)
            .HasFilter("[ReadAtUtc] IS NULL");
    }
}
