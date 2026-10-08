using System.ComponentModel.DataAnnotations;
using TeamBuilder.Application.Scheduling;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Application.DTOs;

/// <summary>
/// <c>POST /api/v1/venues</c>. A physical venue (Indoor/Outdoor) needs real coordinates and an
/// IANA time zone; a Virtual venue has no coordinates. Nothing is geocoded: the client supplies
/// the coordinates (browser location or manual entry).
/// </summary>
public class CreateVenueDto : IValidatableObject
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [StringLength(200)]
    public string? AddressLine1 { get; set; }

    [StringLength(200)]
    public string? AddressLine2 { get; set; }

    [StringLength(100)]
    public string? City { get; set; }

    [StringLength(100)]
    public string? StateOrProvince { get; set; }

    [StringLength(20)]
    public string? PostalCode { get; set; }

    /// <summary>ISO 3166-1 alpha-2 (two letters); stored upper-case.</summary>
    public string? CountryCode { get; set; }

    [Range(-90.0, 90.0)]
    public decimal? Latitude { get; set; }

    [Range(-180.0, 180.0)]
    public decimal? Longitude { get; set; }

    [StringLength(IanaTimeZone.MaxLength)]
    public string? TimeZoneId { get; set; }

    [Required]
    [EnumDataType(typeof(VenueType))]
    public VenueType? VenueType { get; set; }

    /// <summary>Required on purpose: the host decides whether the address may be shown.</summary>
    [Required]
    [EnumDataType(typeof(VenuePrivacyLevel))]
    public VenuePrivacyLevel? PrivacyLevel { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Name))
            yield return new ValidationResult("Name must not be blank.", [nameof(Name)]);

        if (CountryCode is not null && (CountryCode.Length != 2 || !CountryCode.All(char.IsAsciiLetter)))
            yield return new ValidationResult("CountryCode must be a two-letter ISO 3166-1 alpha-2 code.", [nameof(CountryCode)]);

        if (VenueType == Domain.Enums.VenueType.Virtual)
        {
            if (Latitude is not null || Longitude is not null)
                yield return new ValidationResult("A virtual venue has no coordinates.", [nameof(Latitude), nameof(Longitude)]);
        }
        else if (VenueType is not null)
        {
            if (Latitude is null || Longitude is null)
                yield return new ValidationResult("A physical venue requires Latitude and Longitude.", [nameof(Latitude), nameof(Longitude)]);
            if (TimeZoneId is null)
                yield return new ValidationResult("A physical venue requires an IANA TimeZoneId.", [nameof(TimeZoneId)]);
        }

        if (TimeZoneId is not null && !IanaTimeZone.TryResolve(TimeZoneId, out _, out var timeZoneError))
            yield return new ValidationResult(timeZoneError, [nameof(TimeZoneId)]);
    }
}

/// <summary>
/// A venue as the API shows it to one caller. When <see cref="IsAddressMasked"/> is true the
/// street address, postal code and coordinates are null: the venue is Private and the caller
/// is not entitled to its exact location.
/// </summary>
public class VenueDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? StateOrProvince { get; set; }
    public string? PostalCode { get; set; }
    public string? CountryCode { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string? TimeZoneId { get; set; }
    public VenueType VenueType { get; set; }
    public VenuePrivacyLevel PrivacyLevel { get; set; }
    public bool IsAddressMasked { get; set; }

    /// <summary>Whether the caller created this venue.</summary>
    public bool IsMine { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// The venue block of a discovery result or an occurrence detail, after privacy masking.
/// </summary>
public class OccurrenceVenueDto
{
    public Guid VenueId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? City { get; set; }
    public string? StateOrProvince { get; set; }
    public string? CountryCode { get; set; }

    /// <summary>Null when masked.</summary>
    public string? AddressLine1 { get; set; }

    /// <summary>Null when masked.</summary>
    public string? AddressLine2 { get; set; }

    /// <summary>Null when masked.</summary>
    public string? PostalCode { get; set; }

    /// <summary>Null when masked.</summary>
    public decimal? Latitude { get; set; }

    /// <summary>Null when masked.</summary>
    public decimal? Longitude { get; set; }

    public string? TimeZoneId { get; set; }
    public VenueType VenueType { get; set; }
    public VenuePrivacyLevel PrivacyLevel { get; set; }

    /// <summary>
    /// Server-calculated distance from the search point (discovery only; null on the detail).
    /// For a Private venue it is measured to its ~1 km grid point, so it is approximate.
    /// </summary>
    public double? DistanceMiles { get; set; }

    public bool IsAddressMasked { get; set; }
}
