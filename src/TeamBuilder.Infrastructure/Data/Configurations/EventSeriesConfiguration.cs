using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Infrastructure.Data.Configurations;

public class EventSeriesConfiguration : IEntityTypeConfiguration<EventSeries>
{
    public const string DurationMinutesRangeCheckName = "CK_EventSeries_DurationMinutes_Range";
    public const string MaxParticipantsRangeCheckName = "CK_EventSeries_MaxParticipants_Range";

    /// <summary>Same public range as one-off events (CreateEventDto.MaxParticipants).</summary>
    public const int MinParticipants = 1;
    public const int MaxParticipants = 100000;

    /// <summary>One week. Business validation of durations belongs to the series API.</summary>
    public const int MaxDurationMinutes = 10080;

    public void Configure(EntityTypeBuilder<EventSeries> builder)
    {
        builder.ToTable("EventSeries", t =>
        {
            t.HasCheckConstraint(
                DurationMinutesRangeCheckName,
                $"[DurationMinutes] > 0 AND [DurationMinutes] <= {MaxDurationMinutes}");
            t.HasCheckConstraint(
                MaxParticipantsRangeCheckName,
                $"[MaxParticipants] >= {MinParticipants} AND [MaxParticipants] <= {MaxParticipants}");
        });

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(s => s.Description)
            .HasMaxLength(2000);

        builder.Property(s => s.Category)
            .HasMaxLength(100);

        builder.Property(s => s.Tags)
            .HasMaxLength(500);

        builder.Property(s => s.LocalStartTime)
            .IsRequired();

        builder.Property(s => s.DurationMinutes)
            .IsRequired();

        // No model default: an unset value must fail the CHECK rather than silently become 50.
        builder.Property(s => s.MaxParticipants)
            .IsRequired();

        builder.Property(s => s.TimeZoneId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(s => s.RecurrenceRule)
            .IsRequired()
            .HasMaxLength(1000);

        builder.Property(s => s.SeriesStartDate)
            .IsRequired();

        builder.Property(s => s.Status)
            .IsRequired();

        builder.Property(s => s.CreatedAtUtc)
            .IsRequired();

        builder.Property(s => s.RowVersion)
            .IsRowVersion();

        // Team association is optional historical context: deleting the team keeps the
        // series and clears the link, matching Events.TeamId.
        builder.HasOne(s => s.Team)
            .WithMany()
            .HasForeignKey(s => s.TeamId)
            .OnDelete(DeleteBehavior.SetNull);

        // Deleting the host's account keeps the schedule and clears the host.
        builder.HasOne(s => s.Host)
            .WithMany()
            .HasForeignKey(s => s.HostId)
            .OnDelete(DeleteBehavior.SetNull);

        // A venue still referenced by a series cannot be deleted (NO ACTION).
        builder.HasOne(s => s.Venue)
            .WithMany(v => v.EventSeries)
            .HasForeignKey(s => s.VenueId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => s.TeamId);
        builder.HasIndex(s => s.HostId);
        builder.HasIndex(s => s.VenueId);
        builder.HasIndex(s => s.Status);
    }
}
