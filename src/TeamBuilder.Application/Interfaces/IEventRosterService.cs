using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Application.Interfaces;

/// <summary>
/// Roster requirements and live assignments of event occurrences. Authorization (host-only
/// mutation, orphan handling) is the caller's responsibility; the service enforces data rules.
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

    /// <exception cref="Exceptions.EventOccurrenceNotFoundException">The occurrence does not exist.</exception>
    /// <exception cref="ArgumentException">The role code is invalid.</exception>
    /// <exception cref="InvalidOperationException">
    /// The occurrence is closed (Completed/Cancelled/Archived) or already has a requirement for the role.
    /// </exception>
    Task<RosterRequirementDto> CreateRequirementAsync(Guid occurrenceId, CreateRosterRequirementDto createDto, CancellationToken cancellationToken = default);

    /// <summary>Assignments (all statuses) ordered by creation; null when the occurrence does not exist.</summary>
    Task<PaginatedResult<RosterAssignmentDto>?> GetAssignmentsAsync(Guid occurrenceId, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <exception cref="Exceptions.EventOccurrenceNotFoundException">The occurrence does not exist.</exception>
    /// <exception cref="ArgumentException">
    /// Unknown player, requirement or replaced assignment of this occurrence, or a role code that conflicts with the requirement.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The occurrence is closed, the player already holds a supply assignment for it, the
    /// requirement is already filled (no overbooking), the requirement changed concurrently, or
    /// the replaced assignment still holds supply.
    /// </exception>
    Task<RosterAssignmentDto> CreateAssignmentAsync(Guid occurrenceId, CreateRosterAssignmentDto createDto, RosterAssignmentSource source, CancellationToken cancellationToken = default);

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
    /// Ends a live assignment on the host's behalf (exit reason HostRemoved). Host
    /// authorization is the caller's responsibility.
    /// </summary>
    /// <exception cref="Exceptions.EventOccurrenceNotFoundException">The occurrence does not exist.</exception>
    /// <exception cref="Exceptions.RosterAssignmentNotFoundException">The assignment is not one of this occurrence.</exception>
    /// <exception cref="Exceptions.RosterConflictException">Closed occurrence, assignment already ended, or concurrent change.</exception>
    Task<RosterAssignmentDto> RemoveAsync(Guid occurrenceId, Guid assignmentId, CancellationToken cancellationToken = default);
}
