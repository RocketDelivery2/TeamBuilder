namespace TeamBuilder.Domain.Enums;

/// <summary>
/// How much of a venue's physical location may be shown. It never controls who can see that a
/// game exists: a Private venue's games are still discoverable, only the exact street address
/// and coordinates are withheld from everyone except the occurrence's host and its current
/// participants.
/// </summary>
/// <remarks>The numeric values are persisted (and appear in a CHECK constraint and the
/// <c>Venues.SearchLocation</c> computed column); never renumber them.</remarks>
public enum VenuePrivacyLevel
{
    /// <summary>A public place (community gym, park court, arena): the address may be shown.</summary>
    Public = 1,

    /// <summary>
    /// A residential driveway, backyard court, private club or invite-only location: discovery
    /// shows only the name, city/state and an approximate distance.
    /// </summary>
    Private = 2
}
