using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Application.Interfaces;

/// <summary>
/// Roster requirements and live assignments of event occurrences. Host-only writes take the
/// caller's player id and enforce host authority themselves, re-validated at commit time
/// against the occurrence RowVersion; the service also enforces every data rule.
/// </summary>
public interface IEventRosterService
{
    /// <summary>
    /// Readiness projection (totals, requirements, all assignments) computed from one read of
    /// requirements and one of assignments; null when the occurrence does not exist.
    /// </summary>
    Task<RosterSummaryDto?> GetSummaryAsync(Guid occurrenceId, CancellationToken cancellationToken = default);

    /// <summary>Requirements of the occurrence with derived supply; null when the occurrence does not exist.</summary>
    Task<IReadOnlyList<RosterRequirementDto>?> GetRequirementsAsync(Guid occurrenceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Host-only: adds a roster requirement. Order: missing occurrence, no host (409
    /// OccurrenceHasNoHost), non-host (403), closed occurrence (409 OccurrenceClosed), duplicate
    /// role (409 DuplicateRequirementRole). Host authority and status are re-validated at commit
    /// time; a stale former host gets 403 even when the transfer lands mid-request.
    /// </summary>
    /// <exception cref="Exceptions.EventOccurrenceNotFoundException">The occurrence does not exist.</exception>
    /// <exception cref="Exceptions.OccurrenceHostForbiddenException">The caller is not the occurrence's host.</exception>
    /// <exception cref="ArgumentException">The role code or count is invalid.</exception>
    /// <exception cref="Exceptions.RosterConflictException">
    /// No host, closed occurrence, duplicate role, or repeated concurrent change (RosterChanged).
    /// </exception>
    Task<RosterRequirementDto> CreateRequirementAsync(Guid occurrenceId, CreateRosterRequirementDto createDto, Guid hostPlayerId, CancellationToken cancellationToken = default);

    /// <summary>Assignments (all statuses) ordered by creation; null when the occurrence does not exist.</summary>
    Task<PaginatedResult<RosterAssignmentDto>?> GetAssignmentsAsync(Guid occurrenceId, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Host-only: assigns any existing player (source Host) to a requirement of this occurrence,
    /// through the same allocation (duplicate check, capacity guard) as player self-claim.
    /// RequirementId is mandatory (400 RequirementIdRequired); a requirement of another
    /// occurrence is 400 RequirementNotOnOccurrence. Host authority and status are re-validated
    /// at commit time as for <see cref="CreateRequirementAsync"/>.
    /// </summary>
    /// <exception cref="Exceptions.EventOccurrenceNotFoundException">The occurrence does not exist.</exception>
    /// <exception cref="Exceptions.OccurrenceHostForbiddenException">The caller is not the occurrence's host.</exception>
    /// <exception cref="ArgumentException">
    /// Missing or foreign requirement (<see cref="Exceptions.RosterValidationException"/>), unknown
    /// player or replaced assignment, or a role code that conflicts with the requirement.
    /// </exception>
    /// <exception cref="Exceptions.RosterConflictException">
    /// No host, closed occurrence, the player already holds a supply assignment for it, the
    /// requirement is already filled (no overbooking), repeated concurrent change, or the
    /// replaced assignment still holds supply.
    /// </exception>
    Task<RosterAssignmentDto> CreateAssignmentAsync(Guid occurrenceId, CreateRosterAssignmentDto createDto, Guid hostPlayerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Player self-claim: <paramref name="playerId"/> takes one unit of open quantity on a
    /// requirement through the same allocation (duplicate check, capacity guard) as host
    /// assignment, as a Confirmed assignment with source Player. When the player already holds
    /// a live assignment on the same requirement, that assignment is returned with
    /// <see cref="RosterClaimResult.Created"/> false (safe retry).
    /// </summary>
    /// <exception cref="Exceptions.EventOccurrenceNotFoundException">The occurrence does not exist.</exception>
    /// <exception cref="Exceptions.RosterRequirementNotFoundException">The requirement is not one of this occurrence.</exception>
    /// <exception cref="ArgumentException">The replaced assignment is unknown or on another requirement.</exception>
    /// <exception cref="Exceptions.RosterConflictException">
    /// Closed occurrence, already participating on another requirement, requirement full,
    /// concurrent change (retryable), or the replaced assignment still holds supply.
    /// </exception>
    Task<RosterClaimResult> ClaimAsync(Guid occurrenceId, Guid playerId, ClaimRosterSpotDto claimDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// The holder ends their own live assignment (exit reason PlayerLeft). The row is kept as
    /// history; its open quantity reopens in the same commit.
    /// </summary>
    /// <exception cref="Exceptions.EventOccurrenceNotFoundException">The occurrence does not exist.</exception>
    /// <exception cref="Exceptions.RosterAssignmentNotFoundException">The assignment is not one of this occurrence.</exception>
    /// <exception cref="Exceptions.RosterAssignmentForbiddenException">The caller does not hold the assignment.</exception>
    /// <exception cref="Exceptions.RosterConflictException">Closed occurrence, assignment already ended, or concurrent change.</exception>
    Task<RosterAssignmentDto> LeaveAsync(Guid occurrenceId, Guid assignmentId, Guid callerPlayerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends a live assignment on the host's behalf (exit reason HostRemoved). Host authority of
    /// <paramref name="hostPlayerId"/> is enforced here and re-validated at commit time.
    /// </summary>
    /// <exception cref="Exceptions.EventOccurrenceNotFoundException">The occurrence does not exist.</exception>
    /// <exception cref="Exceptions.OccurrenceHostForbiddenException">The caller is not the occurrence's host.</exception>
    /// <exception cref="Exceptions.RosterAssignmentNotFoundException">The assignment is not one of this occurrence.</exception>
    /// <exception cref="Exceptions.RosterConflictException">
    /// No host, closed occurrence, assignment already ended, or repeated concurrent change.
    /// </exception>
    Task<RosterAssignmentDto> RemoveAsync(Guid occurrenceId, Guid assignmentId, Guid hostPlayerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A host lifecycle step on a live assignment: check-in (Confirmed to CheckedIn), activate
    /// (CheckedIn to Active) or no-show (Confirmed/CheckedIn to NoShow, which releases the
    /// spot). The row is updated in place, never deleted. Same authority and ordering as
    /// <see cref="RemoveAsync"/>.
    /// </summary>
    /// <exception cref="Exceptions.EventOccurrenceNotFoundException">The occurrence does not exist.</exception>
    /// <exception cref="Exceptions.OccurrenceHostForbiddenException">The caller is not the occurrence's host.</exception>
    /// <exception cref="Exceptions.RosterAssignmentNotFoundException">The assignment is not one of this occurrence.</exception>
    /// <exception cref="Exceptions.RosterConflictException">
    /// No host, closed occurrence, assignment already ended, a step not allowed from the current
    /// status, or repeated concurrent change.
    /// </exception>
    Task<RosterAssignmentDto> TransitionAsync(
        Guid occurrenceId,
        Guid assignmentId,
        Guid hostPlayerId,
        RosterAssignmentTransition transition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The occurrences on <paramref name="playerId"/>'s own schedule: those where the player has
    /// a live assignment (or any assignment, with <see cref="PlayerOccurrenceQuery.IncludeTerminal"/>),
    /// not over yet relative to FromUtc (default now), nearest first, keyset-paged. A constant
    /// number of queries per page, whatever its size.
    /// </summary>
    /// <exception cref="ArgumentException">Invalid window or cursor.</exception>
    Task<PlayerOccurrencePageDto> GetPlayerOccurrencesAsync(Guid playerId, PlayerOccurrenceQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// The game-page projection of one occurrence (see <see cref="OccurrenceDetailDto"/>) in a
    /// fixed number of queries: occurrence with host and venue, requirements, live participants.
    /// <paramref name="callerPlayerId"/> (null when anonymous or unlinked) fills the caller's
    /// relationship. Null when the occurrence does not exist.
    /// </summary>
    Task<OccurrenceDetailDto?> GetOccurrenceDetailAsync(Guid occurrenceId, Guid? callerPlayerId, CancellationToken cancellationToken = default);
}
