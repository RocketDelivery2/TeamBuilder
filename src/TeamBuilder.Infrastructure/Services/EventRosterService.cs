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
/// are the participation authority for roster operations, computed only through
/// <see cref="RosterState.Compute"/>; the legacy <see cref="EventOccurrence.CurrentParticipantCount"/>
/// is neither read nor written here.
/// </summary>
public class EventRosterService : IEventRosterService
{
    public const string DuplicateRequirementMessage =
        "This event already has a roster requirement for that role.";
    public const string DuplicateSupplyAssignmentMessage =
        "This player already holds a roster spot for this event.";
    public const string RequirementFilledMessage =
        "This roster requirement is already filled.";
    public const string RequirementChangedMessage =
        "The roster requirement changed while the assignment was being saved. Please try again.";
    public const string ClosedOccurrenceMessage =
        "Roster changes are not allowed for a completed, cancelled or archived event.";

    private readonly TeamBuilderDbContext _context;
    private readonly TimeProvider _timeProvider;

    public EventRosterService(TeamBuilderDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<RosterSummaryDto?> GetSummaryAsync(Guid occurrenceId, CancellationToken cancellationToken = default)
    {
        if (!await _context.Events.AnyAsync(e => e.Id == occurrenceId, cancellationToken))
            return null;

        var requirements = await LoadRequirementsAsync(occurrenceId, cancellationToken);
        var assignments = (await ProjectAssignments(_context.RosterAssignments.Where(a => a.OccurrenceId == occurrenceId))
            .ToListAsync(cancellationToken))
            .Select(MarkUtc)
            .ToList();

        var snapshot = RosterState.Compute(
            occurrenceId,
            requirements.Select(r => (r.Id, r.RequiredCount)),
            assignments.Select(a => (a.RequirementId, a.Status)));

        return new RosterSummaryDto
        {
            OccurrenceId = occurrenceId,
            RequiredCount = snapshot.TotalRequiredCount,
            SupplyCount = snapshot.TotalSupplyCount,
            OpenQuantity = snapshot.TotalOpenQuantity,
            IsRosterReady = snapshot.IsRosterReady,
            Requirements = requirements.Select(r => MapToDto(r, snapshot)).ToList(),
            Assignments = assignments
        };
    }

    public async Task<IReadOnlyList<RosterRequirementDto>?> GetRequirementsAsync(Guid occurrenceId, CancellationToken cancellationToken = default)
    {
        if (!await _context.Events.AnyAsync(e => e.Id == occurrenceId, cancellationToken))
            return null;

        var requirements = await LoadRequirementsAsync(occurrenceId, cancellationToken);
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

        // A new requirement has no linked assignments yet.
        return MapToDto(requirement, RosterState.Compute(occurrenceId, [(requirement.Id, requirement.RequiredCount)], []));
    }

    public async Task<PaginatedResult<RosterAssignmentDto>?> GetAssignmentsAsync(Guid occurrenceId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        if (!await _context.Events.AnyAsync(e => e.Id == occurrenceId, cancellationToken))
            return null;

        var query = _context.RosterAssignments.Where(a => a.OccurrenceId == occurrenceId);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await ProjectAssignments(query)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PaginatedResult<RosterAssignmentDto>
        {
            Items = items.Select(MarkUtc).ToList(),
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
        var player = await _context.Players
            .Where(p => p.Id == playerId)
            .Select(p => new { p.Username, p.DisplayName })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new ArgumentException("PlayerId does not reference an existing player.");

        RosterRequirement? requirement = null;
        if (createDto.RequirementId is { } requirementId)
        {
            // Tracked: its RowVersion guards the capacity check below.
            requirement = await _context.RosterRequirements
                .FirstOrDefaultAsync(r => r.Id == requirementId && r.OccurrenceId == occurrenceId, cancellationToken)
                ?? throw new ArgumentException("RequirementId does not reference a roster requirement of this event.");

            if (roleCode is not null && !string.Equals(roleCode, requirement.RoleCode, StringComparison.Ordinal))
                throw new ArgumentException($"RoleCode must match the requirement's role code '{requirement.RoleCode}' or be omitted.");

            roleCode = requirement.RoleCode;
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

        if (requirement is not null)
        {
            // No overbooking: there is no existing rule allowing supply above RequiredCount.
            // The count is read after the requirement (and its RowVersion), and the requirement
            // row is always UPDATEd with a RowVersion check in the same transaction as the
            // INSERT, so two concurrent fills of the last open quantity cannot both commit.
            var supply = await _context.RosterAssignments.CountAsync(
                a => a.RequirementId == requirement.Id && supplyStatuses.Contains(a.Status),
                cancellationToken);
            if (RosterState.OpenQuantity(requirement.RequiredCount, supply) == 0)
                throw new InvalidOperationException(RequirementFilledMessage);

            _context.Entry(requirement).Property(r => r.RequiredCount).IsModified = true;
        }

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
        StampStatusTimestamp(assignment, _timeProvider.GetUtcNow().UtcDateTime);

        _context.RosterAssignments.Add(assignment);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new InvalidOperationException(RequirementChangedMessage, ex);
        }
        catch (DbUpdateException ex) when (RosterConflictClassifier.IsDuplicateSupplyAssignment(ex))
        {
            throw new InvalidOperationException(DuplicateSupplyAssignmentMessage, ex);
        }

        return MapToDto(assignment, player.Username, player.DisplayName);
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

    private async Task<List<RosterRequirement>> LoadRequirementsAsync(Guid occurrenceId, CancellationToken cancellationToken)
    {
        return await _context.RosterRequirements
            .AsNoTracking()
            .Where(r => r.OccurrenceId == occurrenceId)
            .OrderBy(r => r.CreatedAtUtc)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task<RosterSupplySnapshot> ComputeSnapshotAsync(
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
            occurrenceId,
            requirements.Select(r => (r.Id, r.RequiredCount)),
            assignments.Select(a => (a.RequirementId, a.Status)));
    }

    /// <summary>
    /// Public assignment projection in creation order. Only public player fields (username,
    /// display name) are selected: never email or identity data.
    /// </summary>
    private static IQueryable<RosterAssignmentDto> ProjectAssignments(IQueryable<RosterAssignment> query)
    {
        return query
            .AsNoTracking()
            .OrderBy(a => a.CreatedAtUtc)
            .ThenBy(a => a.Id)
            .Select(a => new RosterAssignmentDto
            {
                Id = a.Id,
                OccurrenceId = a.OccurrenceId,
                PlayerId = a.PlayerId,
                Username = a.Player.Username,
                DisplayName = a.Player.DisplayName,
                RequirementId = a.RequirementId,
                RoleCode = a.RoleCode,
                SourceRoleLabel = a.SourceRoleLabel,
                Status = a.Status,
                Source = a.Source,
                ReservedAtUtc = a.ReservedAtUtc,
                ConfirmedAtUtc = a.ConfirmedAtUtc,
                CheckedInAtUtc = a.CheckedInAtUtc,
                ActivatedAtUtc = a.ActivatedAtUtc,
                DepartedAtUtc = a.DepartedAtUtc,
                ExitReason = a.ExitReason,
                ReplacedAssignmentId = a.ReplacedAssignmentId,
                CreatedAtUtc = a.CreatedAtUtc,
                UpdatedAtUtc = a.UpdatedAtUtc
            });
    }

    private static string? NormalizeRoleCode(string? roleCode)
    {
        if (roleCode is null)
            return null;

        if (!RosterRoleCodes.TryNormalize(roleCode, out var normalized, out var error))
            throw new ArgumentException(error);

        return normalized;
    }

    private static RosterRequirementDto MapToDto(RosterRequirement requirement, RosterSupplySnapshot snapshot)
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

    private static RosterAssignmentDto MapToDto(RosterAssignment assignment, string username, string? displayName)
    {
        return MarkUtc(new RosterAssignmentDto
        {
            Id = assignment.Id,
            OccurrenceId = assignment.OccurrenceId,
            PlayerId = assignment.PlayerId,
            Username = username,
            DisplayName = displayName,
            RequirementId = assignment.RequirementId,
            RoleCode = assignment.RoleCode,
            SourceRoleLabel = assignment.SourceRoleLabel,
            Status = assignment.Status,
            Source = assignment.Source,
            ReservedAtUtc = assignment.ReservedAtUtc,
            ConfirmedAtUtc = assignment.ConfirmedAtUtc,
            CheckedInAtUtc = assignment.CheckedInAtUtc,
            ActivatedAtUtc = assignment.ActivatedAtUtc,
            DepartedAtUtc = assignment.DepartedAtUtc,
            ExitReason = assignment.ExitReason,
            ReplacedAssignmentId = assignment.ReplacedAssignmentId,
            CreatedAtUtc = assignment.CreatedAtUtc,
            UpdatedAtUtc = assignment.UpdatedAtUtc
        });
    }

    /// <summary>datetime2 drops DateTime.Kind; values read back are UTC by convention.</summary>
    private static RosterAssignmentDto MarkUtc(RosterAssignmentDto dto)
    {
        dto.ReservedAtUtc = AsUtc(dto.ReservedAtUtc);
        dto.ConfirmedAtUtc = AsUtc(dto.ConfirmedAtUtc);
        dto.CheckedInAtUtc = AsUtc(dto.CheckedInAtUtc);
        dto.ActivatedAtUtc = AsUtc(dto.ActivatedAtUtc);
        dto.DepartedAtUtc = AsUtc(dto.DepartedAtUtc);
        dto.CreatedAtUtc = EventService.AsUtc(dto.CreatedAtUtc);
        dto.UpdatedAtUtc = AsUtc(dto.UpdatedAtUtc);
        return dto;
    }

    private static DateTime? AsUtc(DateTime? value) => value is { } v ? EventService.AsUtc(v) : null;
}
