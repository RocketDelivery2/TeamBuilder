using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Infrastructure.Data.Configurations;

public class VenueConfiguration : IEntityTypeConfiguration<Venue>
{
    public const string LatitudeRangeCheckName = "CK_Venues_Latitude_Range";
    public const string LongitudeRangeCheckName = "CK_Venues_Longitude_Range";

    public void Configure(EntityTypeBuilder<Venue> builder)
    {
        builder.HasKey(v => v.Id);

        // Coordinates are optional in this foundation (no verified coordinates exist for
        // migrated events, and virtual venues may have none), but a stored value must be a
        // real coordinate. NULL passes a CHECK constraint.
        builder.ToTable(t =>
        {
            t.HasCheckConstraint(LatitudeRangeCheckName, "[Latitude] >= -90 AND [Latitude] <= 90");
            t.HasCheckConstraint(LongitudeRangeCheckName, "[Longitude] >= -180 AND [Longitude] <= 180");
        });

        builder.Property(v => v.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(v => v.AddressLine1)
            .HasMaxLength(200);

        builder.Property(v => v.AddressLine2)
            .HasMaxLength(200);

        builder.Property(v => v.City)
            .HasMaxLength(100);

        builder.Property(v => v.StateOrProvince)
            .HasMaxLength(100);

        builder.Property(v => v.PostalCode)
            .HasMaxLength(20);

        builder.Property(v => v.CountryCode)
            .HasMaxLength(2);

        builder.Property(v => v.Latitude)
            .HasPrecision(9, 6);

        builder.Property(v => v.Longitude)
            .HasPrecision(9, 6);

        builder.Property(v => v.TimeZoneId)
            .HasMaxLength(100);

        builder.Property(v => v.VenueType)
            .IsRequired();

        builder.Property(v => v.CreatedAtUtc)
            .IsRequired();

        builder.Property(v => v.RowVersion)
            .IsRowVersion();
    }
}
