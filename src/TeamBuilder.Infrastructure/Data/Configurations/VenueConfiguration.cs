using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Infrastructure.Data.Configurations;

public class VenueConfiguration : IEntityTypeConfiguration<Venue>
{
    public const string LatitudeRangeCheckName = "CK_Venues_Latitude_Range";
    public const string LongitudeRangeCheckName = "CK_Venues_Longitude_Range";
    public const string PrivacyLevelRangeCheckName = "CK_Venues_PrivacyLevel_Range";

    /// <summary>
    /// Persisted computed <c>geography</c> column used only by discovery's raw SQL. It is not
    /// part of the EF model (the domain has no spatial types); the migration
    /// <c>AddVenueOwnershipPrivacyAndSearchLocation</c> creates it and its spatial index with
    /// <see cref="SearchLocationColumnSql"/>. NULL for virtual venues and for venues without
    /// both coordinates, so they can never match a radius search. A Private venue's point is
    /// snapped to a 0.01 degree grid (about 1 km), so distances, ordering and radius probing
    /// never reveal its exact position. Because the column depends on Latitude, Longitude,
    /// VenueType and PrivacyLevel, a future migration that alters one of those columns must
    /// drop and recreate it (and the spatial index) around the change.
    /// </summary>
    public const string SearchLocationColumnName = "SearchLocation";
    public const string SearchLocationIndexName = "SIX_Venues_SearchLocation";

    /// <summary><c>geography::Point</c> takes (latitude, longitude, SRID) in that order.</summary>
    public const string SearchLocationColumnSql =
        "CASE WHEN [VenueType] <> 3 AND [Latitude] IS NOT NULL AND [Longitude] IS NOT NULL THEN " +
        "CASE WHEN [PrivacyLevel] = 2 THEN geography::Point(ROUND([Latitude], 2), ROUND([Longitude], 2), 4326) " +
        "ELSE geography::Point([Latitude], [Longitude], 4326) END END";

    /// <summary>The grid a Private venue's search point is snapped to, in decimal degrees.</summary>
    public const int PrivateSearchLocationDecimals = 2;

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
            t.HasCheckConstraint(PrivacyLevelRangeCheckName, "[PrivacyLevel] >= 1 AND [PrivacyLevel] <= 2");
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

        // Every venue that existed before the Venue API is a system/shared venue (no creator)
        // at a public place: the migration backfills them as Public (1) without fabricating
        // anything else. New venues always state their privacy explicitly.
        builder.Property(v => v.PrivacyLevel)
            .IsRequired();

        // Deleting a player keeps the venues they created (and every occurrence at them); the
        // venue just loses its creator.
        builder.HasOne(v => v.CreatedByPlayer)
            .WithMany()
            .HasForeignKey(v => v.CreatedByPlayerId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Ignore(v => v.IsPhysical);

        builder.Property(v => v.CreatedAtUtc)
            .IsRequired();

        builder.Property(v => v.RowVersion)
            .IsRowVersion();
    }
}
