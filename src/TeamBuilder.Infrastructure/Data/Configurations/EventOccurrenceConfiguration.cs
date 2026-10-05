using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Infrastructure.Data.Configurations;

/// <summary>
/// EventOccurrence is the entity formerly named TeamEvent. It deliberately keeps the physical
/// <c>Events</c> table: existing rows already are occurrences and were renamed in place, never
/// copied into a second table.
/// </summary>
public class EventOccurrenceConfiguration : IEntityTypeConfiguration<EventOccurrence>
{
    public const string TableName = "Events";
    public const string SeriesStartUniqueIndexName = "UX_Events_SeriesId_ScheduledStartUtc";

    public void Configure(EntityTypeBuilder<EventOccurrence> builder)
    {
        builder.ToTable(TableName);

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(e => e.Description)
            .HasMaxLength(2000);

        builder.Property(e => e.ScheduledStartUtc)
            .IsRequired();

        // Nullable on purpose: legacy events recorded only a start, and no end is invented.
        builder.Property(e => e.ScheduledEndUtc);

        builder.Property(e => e.IsDetached)
            .IsRequired();

        builder.Property(e => e.Status)
            .IsRequired();

        builder.Property(e => e.Category)
            .HasMaxLength(100);

        builder.Property(e => e.Tags)
            .HasMaxLength(500);

        builder.Property(e => e.LegacyLocation)
            .HasMaxLength(200);

        builder.Ignore(e => e.DisplayLocation);

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

        // Hard-deleting a series must never delete its history: occurrences are detached
        // from it (SET NULL) instead.
        builder.HasOne(e => e.Series)
            .WithMany(s => s.Occurrences)
            .HasForeignKey(e => e.SeriesId)
            .OnDelete(DeleteBehavior.SetNull);

        // A venue that still has occurrences cannot be deleted (NO ACTION), so the venue
        // association of past events is never silently erased.
        builder.HasOne(e => e.Venue)
            .WithMany(v => v.Occurrences)
            .HasForeignKey(e => e.VenueId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => e.ScheduledStartUtc);
        builder.HasIndex(e => e.SeriesId);

        // Recurrence materialization idempotency boundary: a series never has two occurrences
        // at the same instant. One-off occurrences (no series) are unaffected.
        builder.HasIndex(e => new { e.SeriesId, e.ScheduledStartUtc })
            .IsUnique()
            .HasDatabaseName(SeriesStartUniqueIndexName)
            .HasFilter("[SeriesId] IS NOT NULL");
        builder.HasIndex(e => e.VenueId);
        builder.HasIndex(e => e.Status);
        builder.HasIndex(e => e.Category);
        builder.HasIndex(e => e.Region);
        builder.HasIndex(e => e.CreatedAtUtc);
    }
}
