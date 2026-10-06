namespace TeamBuilder.Domain.Enums;

/// <summary>
/// Live participation state of a <see cref="Entities.RosterAssignment"/>. Deliberately small:
/// requests and waitlists are separate intent concepts and never appear here.
/// Reserved, Confirmed, CheckedIn and Active hold roster supply (see <see cref="RosterState"/>);
/// Departed, NoShow and Cancelled are historical and do not.
/// </summary>
/// <remarks>
/// The numeric values are persisted and appear in the SQL filtered unique index
/// <c>UX_RosterAssignments_OccurrenceId_PlayerId_Supply</c>; never renumber them.
/// </remarks>
public enum RosterAssignmentStatus
{
    Reserved = 1,
    Confirmed = 2,
    CheckedIn = 3,
    Active = 4,
    Departed = 5,
    NoShow = 6,
    Cancelled = 7
}
