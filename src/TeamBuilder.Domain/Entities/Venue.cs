using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Domain.Entities;

/// <summary>
/// Where an occurrence takes place. Coordinates and time zone are nullable because migrated
/// legacy venues carry no verified location and virtual venues have no physical coordinates;
/// <c>POST /api/v1/venues</c> requires both for a physical (Indoor/Outdoor) venue. Coordinates
/// are plain decimals: the SQL Server <c>geography</c> used by discovery is a persisted computed
/// column derived from them in the database, so the domain carries no spatial types.
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

    /// <summary>Whether the exact address and coordinates may be shown to anyone.</summary>
    public VenuePrivacyLevel PrivacyLevel { get; set; } = VenuePrivacyLevel.Public;

    /// <summary>
    /// The player who created this venue, or null for a system, imported or shared venue
    /// (every venue that existed before the Venue API).
    /// </summary>
    public Guid? CreatedByPlayerId { get; set; }
    public Player? CreatedByPlayer { get; set; }

    /// <summary>True for Indoor/Outdoor venues, which are the only ones radius search can find.</summary>
    public bool IsPhysical => VenueType != VenueType.Virtual;

    public ICollection<EventSeries> EventSeries { get; set; } = new List<EventSeries>();
    public ICollection<EventOccurrence> Occurrences { get; set; } = new List<EventOccurrence>();
}
