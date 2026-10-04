namespace TeamBuilder.Domain.Enums;

/// <summary>Lifecycle of a recurring series. Deliberately separate from <see cref="EventStatus"/>.</summary>
public enum EventSeriesStatus
{
    Active = 1,
    Paused = 2,
    Cancelled = 3,
    Completed = 4
}
