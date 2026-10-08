namespace TeamBuilder.Application.Exceptions;

/// <summary>
/// Thrown when a player tries to hold a game at someone else's Private venue. Attaching it
/// would make them the host and so reveal its exact address. Mapped to 403.
/// </summary>
public sealed class VenueAttachForbiddenException : Exception
{
    public VenueAttachForbiddenException(Guid venueId)
        : base("A private venue can only be used by the player who created it.")
    {
        VenueId = venueId;
    }

    public Guid VenueId { get; }
}
