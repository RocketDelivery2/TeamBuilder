using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Persistence;

namespace TeamBuilder.Infrastructure.Services;

/// <summary>
/// Roster requirements and live assignments. Supply-status <see cref="RosterAssignment"/> rows
/// are the participation authority for roster operations; the legacy
/// <see cref="EventOccurrence.CurrentParticipantCount"/> is neither read nor written here.
/// </summary>
public class EventRosterService : IEventRosterService
{
    public const string DuplicateRequirementMessage =
        "This event already has a roster requirement for that role.";
    public const string DuplicateSupplyAssignmentMessage =
        "This player already holds a roster spot for this event.";
    public const string ClosedOccurrenceMessage =
        "Roster changes are not allowed for a completed, cancelled or archived event.";

    private readonly TeamBuilderDbContext _context;
    private readonly TimeProvider _timeProvider;

    public EventRosterService(TeamBuilderDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<RosterRequirementDto>?> GetRequirementsAsync(Guid occurrenceId, CancellationToken cancellationToken = default)
    {
        if (!await _context.Events.AnyAsync(e => e.Id == occurrenceId, cancellationToken))
            return null;

        var requirements = await _context.RosterRequirements
            .AsNoTracking()
            .Where(r => r.OccurrenceId == occurrenceId)
            .OrderBy(r => r.CreatedAtUtc)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);

        var snapshot = await ComputeSnapshotAsync(occurrenceId, requirements, cancellationToken);
        return requirements.Select(r => MapToDto(r, snapshot)).ToList();
    }

    public async Task<RosterRequirementDto> CreateRequirementAsync(Guid occurrenceId, CreateRosterRequirementDto createDto, CancellationToken cancellationToken = default)
    {
        if (createDto.RequiredCount is not { } requiredCount)
            throw new ArgumentException("RequiredCount is required.");

        var roleCode = NormalizeRoleCode(createDto.RoleCode) ?? RosterRoleCodes.Participant;

        await EnsureOpenOccurrenceAsync(occurrenceId, cancellationToken);

        if (await _context.RosterRequirements.AnyAsync(r => r.OccurrenceId == occurrenceId && r.RoleCode == roleCode, cancellationToken))
            throw new InvalidOperationException(DuplicateRequirementMessage);

        var requirement = new RosterRequirement
        {
            Id = Guid.NewGuid(),
            OccurrenceId = occurrenceId,
            RoleCode = roleCode,
            DisplayPosition = createDto.DisplayPosition,
            SourceRoleLabel = createDto.SourceRoleLabel,
            RequiredCount = requiredCount
        };

        _context.RosterRequirements.Add(requirement);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (RosterConflictClassifier.IsDuplicateRequirementRole(ex))
        {
            throw new InvalidOperationException(DuplicateRequirementMessage, ex);
        }

        var snapshot = await ComputeSnapshotAsync(occurrenceId, [requirement], cancellationToken);
        return MapToDto(requirement, snapshot);
    }

    public async Task<PaginatedResult<RosterAssignmentDto>?> GetAssignmentsAsync(Guid occurrenceId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        if (!await _context.Events.AnyAsync(e => e.Id == occurrenceId, cancellationToken))
            return null;

        var query = _context.RosterAssignments
            .AsNoTracking()
            .Where(a => a.OccurrenceId == occurrenceId);

        var totalCount = await query.CountAsync(cancellationToken);

        // Projected so only public player fields are ever read (never Email or identities).
        var items = await query
            .OrderBy(a => a.CreatedAtUtc)
            .ThenBy(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new { Assignment = a, a.Player.Username })
            .ToListAsync(cancellationToken);

        return new PaginatedResult<RosterAssignmentDto>
        {
            Items = items.Select(x => MapToDto(x.Assignment, x.Username)).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<RosterAssignmentDto> CreateAssignmentAsync(
        Guid occurrenceId,
        CreateRosterAssignmentDto createDto,
        RosterAssignmentSource source,
        CancellationToken cancellationToken = default)
    {
        if (createDto.PlayerId is not { } playerId || playerId == Guid.Empty)
            throw new ArgumentException("PlayerId is required.");

        var status = createDto.Status ?? RosterAssignmentStatus.Confirmed;
        if (!RosterState.IsSupply(status))
            throw new ArgumentException("Status must be Reserved, Confirmed, CheckedIn or Active for a new assignment.");

        var roleCode = NormalizeRoleCode(createDto.RoleCode);

        await EnsureOpenOccurrenceAsync(occurrenceId, cancellationToken);

        // Membership is deliberately not consulted: any existing player may participate.
        var username = await _context.Players
            .Where(p => p.Id == playerId)
            .Select(p => p.Username)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new ArgumentException("PlayerId does not reference an existing player.");

        if (createDto.RequirementId is { } requirementId)
        {
            var requirementRole = await _context.RosterRequirements
                .Where(r => r.Id == requirementId && r.OccurrenceId == occurrenceId)
                .Select(r => r.RoleCode)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new ArgumentException("RequirementId does not reference a roster requirement of this event.");

            if (roleCode is not null && !string.Equals(roleCode, requirementRole, StringComparison.Ordinal))
                throw new ArgumentException($"RoleCode must match the requirement's role code '{requirementRole}' or be omitted.");

            roleCode = requirementRole;
        }

        if (createDto.ReplacedAssignmentId is { } replacedId)
        {
            var replacedStatus = await _context.RosterAssignments
                .Where(a => a.Id == replacedId && a.OccurrenceId == occurrenceId)
                .Select(a => (RosterAssignmentStatus?)a.Status)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new ArgumentException("ReplacedAssignmentId does not reference a roster assignment of this event.");

            if (RosterState.IsSupply(replacedStatus))
                throw new InvalidOperationException("The replaced assignment still holds a roster spot; it must have departed, been a no-show or been cancelled.");
        }

        var supplyStatuses = RosterState.SupplyStatuses;
        if (await _context.RosterAssignments.AnyAsync(
                a => a.OccurrenceId == occurrenceId && a.PlayerId == playerId && supplyStatuses.Contains(a.Status),
                cancellationToken))
        {
            throw new InvalidOperationException(DuplicateSupplyAssignmentMessage);
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var assignment = new RosterAssignment
        {
            Id = Guid.NewGuid(),
            OccurrenceId = occurrenceId,
            PlayerId = playerId,
            RequirementId = createDto.RequirementId,
            RoleCode = roleCode,
            SourceRoleLabel = createDto.SourceRoleLabel,
            Status = status,
            Source = source,
            ReplacedAssignmentId = createDto.ReplacedAssignmentId
        };
        StampStatusTimestamp(assignment, nowUtc);

        _context.RosterAssignments.Add(assignment);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (RosterConflictClassifier.IsDuplicateSupplyAssignment(ex))
        {
            throw new InvalidOperationException(DuplicateSupplyAssignmentMessage, ex);
        }

        return MapToDto(assignment, username);
    }

    /// <summary>Records when the assignment entered its (initial) status.</summary>
    internal static void StampStatusTimestamp(RosterAssignment assignment, DateTime nowUtc)
    {
        switch (assignment.Status)
        {
            case RosterAssignmentStatus.Reserved: assignment.ReservedAtUtc = nowUtc; break;
            case RosterAssignmentStatus.Confirmed: assignment.ConfirmedAtUtc = nowUtc; break;
            case RosterAssignmentStatus.CheckedIn: assignment.CheckedInAtUtc = nowUtc; break;
            case RosterAssignmentStatus.Active: assignment.ActivatedAtUtc = nowUtc; break;
            default: throw new ArgumentOutOfRangeException(nameof(assignment), assignment.Status, "Not a supply status.");
        }
    }

    private async Task EnsureOpenOccurrenceAsync(Guid occurrenceId, CancellationToken cancellationToken)
    {
        var status = await _context.Events
            .Where(e => e.Id == occurrenceId)
            .Select(e => (EventStatus?)e.Status)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new EventOccurrenceNotFoundException(occurrenceId);

        if (!RosterState.AcceptsNewRosterMutations(status))
            throw new InvalidOperationException(ClosedOccurrenceMessage);
    }

    private async Task<RosterSnapshot> ComputeSnapshotAsync(
        Guid occurrenceId,
        IReadOnlyCollection<RosterRequirement> requirements,
        CancellationToken cancellationToken)
    {
        var supplyStatuses = RosterState.SupplyStatuses;
        var assignments = await _context.RosterAssignments
            .AsNoTracking()
            .Where(a => a.OccurrenceId == occurrenceId && supplyStatuses.Contains(a.Status))
            .Select(a => new { a.RequirementId, a.Status })
            .ToListAsync(cancellationToken);

        return RosterState.Compute(
            requirements.Select(r => (r.Id, r.RequiredCount)),
            assignments.Select(a => (a.RequirementId, a.Status)));
    }

    private static string? NormalizeRoleCode(string? roleCode)
    {
        if (roleCode is null)
            return null;

        if (!RosterRoleCodes.TryNormalize(roleCode, out var normalized, out var error))
            throw new ArgumentException(error);

        return normalized;
    }

    private static RosterRequirementDto MapToDto(RosterRequirement requirement, RosterSnapshot snapshot)
    {
        var supply = snapshot.Requirements[requirement.Id];
        return new RosterRequirementDto
        {
            Id = requirement.Id,
            OccurrenceId = requirement.OccurrenceId,
            RoleCode = requirement.RoleCode,
            DisplayPosition = requirement.DisplayPosition,
            SourceRoleLabel = requirement.SourceRoleLabel,
            RequiredCount = requirement.RequiredCount,
            SupplyCount = supply.SupplyCount,
            OpenQuantity = supply.OpenQuantity,
            CreatedAtUtc = EventService.AsUtc(requirement.CreatedAtUtc),
            UpdatedAtUtc = AsUtc(requirement.UpdatedAtUtc)
        };
    }

    private static RosterAssignmentDto MapToDto(RosterAssignment assignment, string username)
    {
        return new RosterAssignmentDto
        {
            Id = assignment.Id,
            OccurrenceId = assignment.OccurrenceId,
            PlayerId = assignment.PlayerId,
            Username = username,
            RequirementId = assignment.RequirementId,
            RoleCode = assignment.RoleCode,
            SourceRoleLabel = assignment.SourceRoleLabel,
            Status = assignment.Status,
            Source = assignment.Source,
            ReservedAtUtc = AsUtc(assignment.ReservedAtUtc),
            ConfirmedAtUtc = AsUtc(assignment.ConfirmedAtUtc),
            CheckedInAtUtc = AsUtc(assignment.CheckedInAtUtc),
            ActivatedAtUtc = AsUtc(assignment.ActivatedAtUtc),
            DepartedAtUtc = AsUtc(assignment.DepartedAtUtc),
            ExitReason = assignment.ExitReason,
            ReplacedAssignmentId = assignment.ReplacedAssignmentId,
            CreatedAtUtc = EventService.AsUtc(assignment.CreatedAtUtc),
            UpdatedAtUtc = AsUtc(assignment.UpdatedAtUtc)
        };
    }

    private static DateTime? AsUtc(DateTime? value) => value is { } v ? EventService.AsUtc(v) : null;
}
