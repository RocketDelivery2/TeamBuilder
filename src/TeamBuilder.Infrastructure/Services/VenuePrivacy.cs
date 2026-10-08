using TeamBuilder.Application.DTOs;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Infrastructure.Services;

/// <summary>
/// The single place that decides what of a venue's location a caller may see. A Public venue
/// is always shown in full. A Private venue's street address, postal code and coordinates are
/// shown only to a caller who is entitled (the occurrence's host or a current participant, or
/// the venue's creator); everyone else gets its name, city, state/province and country with
/// <c>IsAddressMasked = true</c>.
/// </summary>
internal static class VenuePrivacy
{
    public const double MetersPerMile = 1609.344;

    /// <summary>The venue columns a projection needs; never includes the creator's identity.</summary>
    internal sealed record VenueRow(
        Guid Id,
        string Name,
        string? AddressLine1,
        string? AddressLine2,
        string? City,
        string? StateOrProvince,
        string? PostalCode,
        string? CountryCode,
        decimal? Latitude,
        decimal? Longitude,
        string? TimeZoneId,
        VenueType VenueType,
        VenuePrivacyLevel PrivacyLevel);

    public static bool IsMasked(VenuePrivacyLevel privacyLevel, bool callerIsEntitled) =>
        privacyLevel != VenuePrivacyLevel.Public && !callerIsEntitled;

    public static OccurrenceVenueDto ToOccurrenceVenue(VenueRow venue, bool callerIsEntitled, double? distanceMeters)
    {
        var masked = IsMasked(venue.PrivacyLevel, callerIsEntitled);
        return new OccurrenceVenueDto
        {
            VenueId = venue.Id,
            Name = venue.Name,
            City = venue.City,
            StateOrProvince = venue.StateOrProvince,
            CountryCode = venue.CountryCode,
            AddressLine1 = masked ? null : venue.AddressLine1,
            AddressLine2 = masked ? null : venue.AddressLine2,
            PostalCode = masked ? null : venue.PostalCode,
            Latitude = masked ? null : venue.Latitude,
            Longitude = masked ? null : venue.Longitude,
            TimeZoneId = venue.TimeZoneId,
            VenueType = venue.VenueType,
            PrivacyLevel = venue.PrivacyLevel,
            DistanceMiles = distanceMeters is { } meters ? DisplayMiles(meters, venue.PrivacyLevel) : null,
            IsAddressMasked = masked
        };
    }

    /// <summary>
    /// Display distance. Public venues to 0.01 mi; Private venues (measured to their ~1 km grid
    /// point) to 0.1 mi, so the number never suggests more precision than it has.
    /// </summary>
    public static double DisplayMiles(double meters, VenuePrivacyLevel privacyLevel) =>
        Math.Round(meters / MetersPerMile, privacyLevel == VenuePrivacyLevel.Public ? 2 : 1, MidpointRounding.AwayFromZero);
}
