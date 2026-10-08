using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// Direct seeding for the discovery suites: venues at known coordinates and games with a
/// chosen roster state, written through EF so the persisted computed geography is produced
/// by SQL Server exactly as for API writes.
/// </summary>
internal static class DiscoverySeeding
{
    /// <summary>Chicago Loop, the default search point.</summary>
    public const double ChicagoLat = 41.878113;
    public const double ChicagoLon = -87.629799;

    /// <summary>Approximate metres per degree of latitude near 42N on WGS 84.</summary>
    private const double MetersPerDegreeLatitude = 111_090;

    /// <summary>A point about <paramref name="miles"/> north of the given one (bands keep wide margins).</summary>
    public static (decimal Lat, decimal Lon) North(double lat, double lon, double miles) =>
        (Math.Round((decimal)(lat + miles * 1609.344 / MetersPerDegreeLatitude), 6), Math.Round((decimal)lon, 6));

    public static Venue Venue(
        decimal? lat,
        decimal? lon,
        VenuePrivacyLevel privacy = VenuePrivacyLevel.Public,
        VenueType type = VenueType.Outdoor,
        string name = "Court",
        Guid? createdBy = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        AddressLine1 = "100 Secret Ln",
        AddressLine2 = "Back gate",
        City = "Chicago",
        StateOrProvince = "IL",
        PostalCode = "60607",
        CountryCode = "US",
        Latitude = lat,
        Longitude = lon,
        TimeZoneId = "America/Chicago",
        VenueType = type,
        PrivacyLevel = privacy,
        CreatedByPlayerId = createdBy
    };

    public static Player Player(string prefix = "p") => new()
    {
        Id = Guid.NewGuid(),
        Username = $"{prefix}-{Guid.NewGuid():N}",
        Email = $"{prefix}-{Guid.NewGuid():N}@example.test"
    };

    /// <summary>
    /// A game at <paramref name="venue"/> with one requirement per (role, required, supply)
    /// entry; supply rows are Confirmed assignments of fresh players. Optional history rows
    /// (Cancelled) never count as supply.
    /// </summary>
    public static EventOccurrence Game(
        TeamBuilderDbContext db,
        Venue? venue,
        DateTime startUtc,
        string? category = "basketball",
        EventStatus status = EventStatus.Open,
        Guid? hostId = null,
        string name = "Pickup",
        int historyRows = 0,
        params (string Role, int Required, int Supply)[] requirements)
    {
        var game = new EventOccurrence
        {
            Id = Guid.NewGuid(),
            Name = name,
            Category = category,
            ScheduledStartUtc = startUtc,
            ScheduledEndUtc = startUtc.AddHours(2),
            Status = status,
            MaxParticipants = 10,
            VenueId = venue?.Id,
            HostId = hostId
        };
        db.Events.Add(game);

        foreach (var (role, required, supply) in requirements)
        {
            var requirement = new RosterRequirement { Id = Guid.NewGuid(), OccurrenceId = game.Id, RoleCode = role, RequiredCount = required };
            db.RosterRequirements.Add(requirement);
            for (var i = 0; i < supply + historyRows; i++)
            {
                var player = Player("supply");
                db.Players.Add(player);
                db.RosterAssignments.Add(new RosterAssignment
                {
                    Id = Guid.NewGuid(),
                    OccurrenceId = game.Id,
                    PlayerId = player.Id,
                    RequirementId = requirement.Id,
                    RoleCode = role,
                    Status = i < supply ? RosterAssignmentStatus.Confirmed : RosterAssignmentStatus.Cancelled,
                    Source = RosterAssignmentSource.Player,
                    ExitReason = i < supply ? null : RosterExitReason.PlayerLeft
                });
            }
        }

        return game;
    }
}
