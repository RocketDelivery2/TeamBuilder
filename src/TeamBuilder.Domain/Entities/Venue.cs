using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Domain.Entities;

/// <summary>
/// Where an occurrence takes place. Persistence foundation only: there is no Venue API yet.
/// Coordinates and time zone are nullable here because migrated legacy events carry no
/// verified location and virtual venues may have no physical coordinates; the future
/// creation API will require them for physical venues.
/// </summary>
public class Venue : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? StateOrProvince { get; set; }
    public string? PostalCode { get; set; }

    /// <summary>ISO 3166-1 alpha-2 country code.</summary>
    public string? CountryCode { get; set; }

    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }

    /// <summary>IANA time zone identifier (for example <c>America/Chicago</c>).</summary>
    public string? TimeZoneId { get; set; }

    public VenueType VenueType { get; set; }

    public ICollection<EventSeries> EventSeries { get; set; } = new List<EventSeries>();
    public ICollection<EventOccurrence> Occurrences { get; set; } = new List<EventOccurrence>();
}
