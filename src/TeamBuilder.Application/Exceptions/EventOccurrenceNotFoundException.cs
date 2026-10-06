namespace TeamBuilder.Application.Exceptions;

/// <summary>
/// Thrown when a roster operation targets an event occurrence that does not exist.
/// Mapped to 404 by the roster endpoints.
/// </summary>
public sealed class EventOccurrenceNotFoundException : Exception
{
    public EventOccurrenceNotFoundException(Guid occurrenceId)
        : base("Event not found.")
    {
        OccurrenceId = occurrenceId;
    }

    public Guid OccurrenceId { get; }
}
