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
}
