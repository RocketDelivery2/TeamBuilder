namespace TeamBuilder.Application.Exceptions;

/// <summary>
/// Thrown when an operation references a venue that does not exist.
/// Mapped to 404 by the event series creation endpoint.
/// </summary>
public sealed class VenueNotFoundException : Exception
{
    public VenueNotFoundException(Guid venueId)
        : base("Venue not found.")
    {
        VenueId = venueId;
    }

    public Guid VenueId { get; }
}
