namespace TeamBuilder.Application.DTOs;

// The public integration contract (teambuilder.roster.v1), served under /api/integration/v1.
// Field names and the lowercase snake_case code values are stable within v1: fields may be
// added, never renamed, removed or retyped. Codes are strings (not the internal enum numbers),
// so a new code is an addition and clients should treat an unknown code as "other".
// JSON Schema: docs/schemas/teambuilder-roster-v1.schema.json.

/// <summary>One game the key's owner hosts, with its roster totals.</summary>
public class IntegrationOccurrenceDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Activity code, e.g. <c>basketball</c>; null when the game has none.</summary>
    public string? ActivityCode { get; set; }

    /// <summary><c>planned</c>, <c>open</c>, <c>in_progress</c>, <c>completed</c>, <c>cancelled</c> or <c>archived</c>.</summary>
    public string Status { get; set; } = string.Empty;

    public DateTime StartsAtUtc { get; set; }
    public DateTime? EndsAtUtc { get; set; }

    /// <summary>IANA time zone of the venue or series, when known.</summary>
    public string? TimeZoneId { get; set; }

    public Guid? SeriesId { get; set; }
    public Guid? TeamId { get; set; }

    /// <summary>Sum of required counts over all requirements.</summary>
    public int RequiredCount { get; set; }

    /// <summary>Participants currently holding a spot.</summary>
    public int SupplyCount { get; set; }

    /// <summary>Spots still open (surplus on one requirement never offsets another).</summary>
    public int OpenQuantity { get; set; }

    /// <summary>True only when the game has requirements and none has an open spot.</summary>
    public bool IsRosterReady { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary><c>GET /api/integration/v1/occurrences/{id}/roster</c>.</summary>
public class IntegrationRosterDto
{
    public const string SchemaId = "teambuilder.roster.v1";

    /// <summary>Always <c>teambuilder.roster.v1</c> for this version.</summary>
    public string Schema { get; set; } = SchemaId;

    public DateTime GeneratedAtUtc { get; set; }
    public IntegrationOccurrenceDto Occurrence { get; set; } = new();

    /// <summary>The game's venue in full (the key's owner hosts it); null when none is attached.</summary>
    public IntegrationVenueDto? Venue { get; set; }

    public IReadOnlyList<IntegrationRequirementDto> Requirements { get; set; } = [];

    /// <summary>Players currently holding a spot, in join order. Departures and no-shows are not listed.</summary>
    public IReadOnlyList<IntegrationParticipantDto> Participants { get; set; } = [];
}

public class IntegrationVenueDto
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

    /// <summary><c>indoor</c>, <c>outdoor</c> or <c>virtual</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary><c>public</c> or <c>private</c>. A private venue's address is only for the host and players.</summary>
    public string Privacy { get; set; } = string.Empty;
}

public class IntegrationRequirementDto
{
    public Guid Id { get; set; }

    /// <summary>Canonical role code within the activity, e.g. <c>participant</c>, <c>guard</c>, <c>goalkeeper</c>.</summary>
    public string RoleCode { get; set; } = string.Empty;

    public string? DisplayPosition { get; set; }
    public string? SourceRoleLabel { get; set; }
    public int RequiredCount { get; set; }
    public int SupplyCount { get; set; }
    public int OpenQuantity { get; set; }
}

/// <summary>A player holding a spot. Carries public profile fields only, never an email or sign-in identity.</summary>
public class IntegrationParticipantDto
{
    public Guid AssignmentId { get; set; }
    public Guid PlayerId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public Guid? RequirementId { get; set; }
    public string? RoleCode { get; set; }

    /// <summary><c>reserved</c>, <c>confirmed</c>, <c>checked_in</c> or <c>active</c>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>How the spot was filled: <c>player</c> (self-claim), <c>host</c>, <c>import</c> or <c>system</c>.</summary>
    public string Source { get; set; } = string.Empty;

    public DateTime JoinedAtUtc { get; set; }
    public DateTime? ConfirmedAtUtc { get; set; }
    public DateTime? CheckedInAtUtc { get; set; }
}
