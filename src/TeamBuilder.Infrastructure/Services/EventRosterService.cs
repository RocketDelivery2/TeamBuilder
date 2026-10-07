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
    public const string AssignmentEndedMessage =
        "This roster assignment has already ended.";
    public const string AssignmentChangedMessage =
        "The roster assignment changed while it was being updated. Please try again.";
    public const string NoHostMessage =
        "This event has no host, so host-only actions are not possible. Contact an administrator.";

    /// <summary>
    /// Attempts of one host-only assignment write. Each concurrency loss re-runs every check on
    /// fresh state; only repeated losses to sibling changes surface as retryable RosterChanged.
    /// </summary>
    internal const int MaxHostUpdateAttempts = 5;

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
            throw new RosterConflictException(RosterConflictCodes.DuplicateRequirementRole, DuplicateRequirementMessage);

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
            throw new RosterConflictException(RosterConflictCodes.DuplicateRequirementRole, DuplicateRequirementMessage, ex);
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
        var player = await LoadPublicPlayerAsync(playerId, cancellationToken)
            ?? throw new ArgumentException("PlayerId does not reference an existing player.");

        RosterRequirement? requirement = null;
        if (createDto.RequirementId is { } requirementId)
        {
            requirement = await LoadTrackedRequirementAsync(occurrenceId, requirementId, cancellationToken)
                ?? throw new ArgumentException("RequirementId does not reference a roster requirement of this event.");

            if (roleCode is not null && !string.Equals(roleCode, requirement.RoleCode, StringComparison.Ordinal))
                throw new ArgumentException($"RoleCode must match the requirement's role code '{requirement.RoleCode}' or be omitted.");
        }

        if (createDto.ReplacedAssignmentId is { } replacedId)
            await EnsureReplaceableAsync(occurrenceId, replacedId, requiredRequirementId: null, cancellationToken);

        var assignment = await AllocateAsync(
            occurrenceId,
            playerId,
            requirement,
            requirement?.RoleCode ?? roleCode,
            createDto.SourceRoleLabel,
            status,
            source,
            createDto.ReplacedAssignmentId,
            cancellationToken);

        return MapToDto(assignment, player.Username, player.DisplayName);
    }

    public async Task<RosterClaimResult> ClaimAsync(
        Guid occurrenceId,
        Guid playerId,
        ClaimRosterSpotDto claimDto,
        CancellationToken cancellationToken = default)
    {
        if (claimDto.RequirementId is not { } requirementId || requirementId == Guid.Empty)
            throw new ArgumentException("RequirementId is required.");

        var occurrenceStatus = await LoadOccurrenceStatusAsync(occurrenceId, cancellationToken);

        // A requirement of another occurrence is indistinguishable from a missing one: 404.
        var requirement = await LoadTrackedRequirementAsync(occurrenceId, requirementId, cancellationToken)
            ?? throw new RosterRequirementNotFoundException(requirementId);

        EnsureOpen(occurrenceStatus);

        // Safe retry: a claim the caller already holds on the same requirement is returned as is.
        var existing = await FindLiveAssignmentAsync(occurrenceId, playerId, cancellationToken);
        if (existing is not null)
            return ExistingClaimOrConflict(existing, requirementId);

        if (claimDto.ReplacesAssignmentId is { } replacedId)
            await EnsureReplaceableAsync(occurrenceId, replacedId, requirementId, cancellationToken);

        var player = await LoadPublicPlayerAsync(playerId, cancellationToken)
            ?? throw new InvalidOperationException("The resolved player no longer exists.");

        try
        {
            var assignment = await AllocateAsync(
                occurrenceId,
                playerId,
                requirement,
                requirement.RoleCode,
                sourceRoleLabel: null,
                RosterAssignmentStatus.Confirmed,
                RosterAssignmentSource.Player,
                claimDto.ReplacesAssignmentId,
                cancellationToken);

            return new RosterClaimResult(MapToDto(assignment, player.Username, player.DisplayName), Created: true);
        }
        catch (RosterConflictException ex) when (ex.Code is RosterConflictCodes.AlreadyParticipating or RosterConflictCodes.RosterChanged or RosterConflictCodes.RequirementFull)
        {
            // A concurrent request by the same caller (double tap, network retry) may have
            // committed first. Re-read outside the failed unit of work: the same claim is
            // returned, anything else keeps the original conflict.
            _context.ChangeTracker.Clear();
            var committed = await FindLiveAssignmentAsync(occurrenceId, playerId, cancellationToken);
            if (committed is null)
                throw;

            return ExistingClaimOrConflict(committed, requirementId);
        }
    }

    public Task<RosterAssignmentDto> LeaveAsync(Guid occurrenceId, Guid assignmentId, Guid callerPlayerId, CancellationToken cancellationToken = default) =>
        EndAssignmentAsync(occurrenceId, assignmentId, RosterExitReason.PlayerLeft, callerPlayerId, cancellationToken);

    public Task<RosterAssignmentDto> RemoveAsync(Guid occurrenceId, Guid assignmentId, Guid hostPlayerId, CancellationToken cancellationToken = default) =>
        HostUpdateAssignmentAsync(
            occurrenceId,
            assignmentId,
            hostPlayerId,
            (assignment, nowUtc) => End(assignment, RosterExitReason.HostRemoved, nowUtc),
            cancellationToken);

    public Task<RosterAssignmentDto> TransitionAsync(
        Guid occurrenceId,
        Guid assignmentId,
        Guid hostPlayerId,
        RosterAssignmentTransition transition,
        CancellationToken cancellationToken = default) =>
        HostUpdateAssignmentAsync(
            occurrenceId,
            assignmentId,
            hostPlayerId,
            (assignment, nowUtc) => ApplyTransition(assignment, transition, nowUtc),
            cancellationToken);

    public async Task<PlayerOccurrencePageDto> GetPlayerOccurrencesAsync(
        Guid playerId,
        PlayerOccurrenceQuery query,
        CancellationToken cancellationToken = default)
    {
        var fromUtc = query.FromUtc is { } from ? EventService.AsUtc(from) : _timeProvider.GetUtcNow().UtcDateTime;
        DateTime? toUtc = query.ToUtc is { } to ? EventService.AsUtc(to) : null;
        if (toUtc < fromUtc)
            throw new ArgumentException("toUtc must not be earlier than fromUtc.");

        PlayerOccurrenceCursor? cursor = query.Cursor is null ? null : PlayerOccurrenceCursor.Parse(query.Cursor);
        var supplyStatuses = RosterState.SupplyStatuses;

        var mine = _context.RosterAssignments.Where(a => a.PlayerId == playerId);
        if (!query.IncludeTerminal)
            mine = mine.Where(a => supplyStatuses.Contains(a.Status));

        // Upcoming = not over yet: its end (or start, when no end was recorded) is at or after
        // fromUtc. An in-progress occurrence is never over, whatever its schedule says.
        var occurrences = _context.Events
            .AsNoTracking()
            .Where(e => mine.Any(a => a.OccurrenceId == e.Id))
            .Where(e => (e.ScheduledEndUtc ?? e.ScheduledStartUtc) >= fromUtc || e.Status == EventStatus.InProgress);
        if (toUtc is { } until)
            occurrences = occurrences.Where(e => e.ScheduledStartUtc < until);
        if (cursor is { } after)
        {
            occurrences = occurrences.Where(e =>
                e.ScheduledStartUtc > after.ScheduledStartUtc ||
                (e.ScheduledStartUtc == after.ScheduledStartUtc && e.Id.CompareTo(after.OccurrenceId) > 0));
        }

        // Query 1 of 4: the page of occurrences (one extra row tells whether another page exists).
        var page = await occurrences
            .OrderBy(e => e.ScheduledStartUtc)
            .ThenBy(e => e.Id)
            .Take(query.PageSize + 1)
            .Select(e => new
            {
                e.Id,
                e.SeriesId,
                e.TeamId,
                e.Name,
                e.ScheduledStartUtc,
                e.ScheduledEndUtc,
                e.Status,
                e.VenueId,
                Location = e.Venue != null ? e.Venue.Name : e.LegacyLocation,
                e.Category,
                e.HostId,
                HostUsername = e.Host != null ? e.Host.Username : null,
                HostDisplayName = e.Host != null ? e.Host.DisplayName : null
            })
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > query.PageSize;
        if (hasMore)
            page.RemoveAt(page.Count - 1);
        if (page.Count == 0)
            return new PlayerOccurrencePageDto();

        var occurrenceIds = page.Select(e => e.Id).ToList();

        // Queries 2-4, each one round trip for the whole page: the caller's own assignments,
        // the requirements, and the supply rows that RosterState.Compute needs.
        var myAssignments = await mine
            .AsNoTracking()
            .Where(a => occurrenceIds.Contains(a.OccurrenceId))
            .Select(a => new { a.Id, a.OccurrenceId, a.Status, a.RoleCode, a.RequirementId, a.CreatedAtUtc })
            .ToListAsync(cancellationToken);

        var requirements = await _context.RosterRequirements
            .AsNoTracking()
            .Where(r => occurrenceIds.Contains(r.OccurrenceId))
            .Select(r => new { r.Id, r.OccurrenceId, r.RoleCode, r.RequiredCount })
            .ToListAsync(cancellationToken);

        var supplyRows = await _context.RosterAssignments
            .AsNoTracking()
            .Where(a => occurrenceIds.Contains(a.OccurrenceId) && supplyStatuses.Contains(a.Status))
            .Select(a => new { a.OccurrenceId, a.RequirementId, a.Status })
            .ToListAsync(cancellationToken);

        var requirementsByOccurrence = requirements.ToLookup(r => r.OccurrenceId);
        var supplyByOccurrence = supplyRows.ToLookup(a => a.OccurrenceId);

        var items = new List<PlayerOccurrenceDto>(page.Count);
        foreach (var occurrence in page)
        {
            // The live assignment when the caller holds one (at most one, by the filtered unique
            // index); otherwise, with includeTerminal, the most recent historical row.
            var mineHere = myAssignments
                .Where(a => a.OccurrenceId == occurrence.Id)
                .OrderByDescending(a => RosterState.IsSupply(a.Status))
                .ThenByDescending(a => a.CreatedAtUtc)
                .ThenByDescending(a => a.Id)
                .First();

            var occurrenceRequirements = requirementsByOccurrence[occurrence.Id].ToList();
            var snapshot = RosterState.Compute(
                occurrence.Id,
                occurrenceRequirements.Select(r => (r.Id, r.RequiredCount)),
                supplyByOccurrence[occurrence.Id].Select(a => (a.RequirementId, a.Status)));

            var requirement = mineHere.RequirementId is { } requirementId
                ? occurrenceRequirements.FirstOrDefault(r => r.Id == requirementId)
                : null;
            var requirementSupply = requirement is null ? null : snapshot.Requirements[requirement.Id];

            items.Add(new PlayerOccurrenceDto
            {
                OccurrenceId = occurrence.Id,
                SeriesId = occurrence.SeriesId,
                TeamId = occurrence.TeamId,
                Name = occurrence.Name,
                ScheduledStartUtc = EventService.AsUtc(occurrence.ScheduledStartUtc),
                ScheduledEndUtc = AsUtc(occurrence.ScheduledEndUtc),
                Status = occurrence.Status,
                VenueId = occurrence.VenueId,
                Location = occurrence.Location,
                Category = occurrence.Category,
                HostPlayerId = occurrence.HostId,
                HostUsername = occurrence.HostUsername,
                HostDisplayName = occurrence.HostDisplayName,
                IsHost = occurrence.HostId == playerId,
                MyAssignmentId = mineHere.Id,
                MyAssignmentStatus = mineHere.Status,
                MyRoleCode = mineHere.RoleCode ?? requirement?.RoleCode,
                MyRequirementId = mineHere.RequirementId,
                RequiredCount = requirementSupply?.RequiredCount,
                SupplyCount = requirementSupply?.SupplyCount,
                OpenQuantity = requirementSupply?.OpenQuantity,
                IsRosterReady = snapshot.IsRosterReady
            });
        }

        var last = page[^1];
        return new PlayerOccurrencePageDto
        {
            Items = items,
            NextCursor = hasMore ? new PlayerOccurrenceCursor(EventService.AsUtc(last.ScheduledStartUtc), last.Id).Encode() : null
        };
    }

    /// <summary>
    /// The shared host-only write on one assignment (lifecycle transitions, host removal).
    /// Host authority and occurrence state are checked, and then re-validated at commit time:
    /// the occurrence row gets a RowVersion-checked UPDATE in the same transaction as the
    /// assignment change, so a host transfer or status change that commits first makes this
    /// save fail instead of being ignored. The assignment's own RowVersion guards it against a
    /// concurrent leave/remove/transition. On a concurrency loss nothing was saved; the whole
    /// operation is re-run from fresh state (a bounded re-validation, never a blind overwrite),
    /// which turns the loss into the real outcome: 403 for a former host, OccurrenceClosed,
    /// AssignmentEnded, AssignmentTransitionInvalid, or success when only a sibling change on
    /// the same occurrence moved its RowVersion.
    /// </summary>
    private async Task<RosterAssignmentDto> HostUpdateAssignmentAsync(
        Guid occurrenceId,
        Guid assignmentId,
        Guid hostPlayerId,
        Action<RosterAssignment, DateTime> apply,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var occurrence = await _context.Events.FirstOrDefaultAsync(e => e.Id == occurrenceId, cancellationToken)
                ?? throw new EventOccurrenceNotFoundException(occurrenceId);

            EnsureHost(occurrence, hostPlayerId);
            EnsureOpen(occurrence.Status);

            var assignment = await _context.RosterAssignments
                .FirstOrDefaultAsync(a => a.Id == assignmentId && a.OccurrenceId == occurrenceId, cancellationToken)
                ?? throw new RosterAssignmentNotFoundException(assignmentId);

            apply(assignment, _timeProvider.GetUtcNow().UtcDateTime);

            // Commit-time host/status check: no value changes, but the UPDATE carries the
            // occurrence RowVersion read above.
            _context.Entry(occurrence).Property(e => e.HostId).IsModified = true;

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _context.ChangeTracker.Clear();
                if (attempt >= MaxHostUpdateAttempts)
                    throw new RosterConflictException(RosterConflictCodes.RosterChanged, AssignmentChangedMessage, ex);
                continue;
            }

            var player = await LoadPublicPlayerAsync(assignment.PlayerId, cancellationToken);
            return MapToDto(assignment, player?.Username ?? string.Empty, player?.DisplayName);
        }
    }

    private static void EnsureHost(EventOccurrence occurrence, Guid callerPlayerId)
    {
        if (occurrence.HostId is null)
            throw new RosterConflictException(RosterConflictCodes.OccurrenceHasNoHost, NoHostMessage);

        if (occurrence.HostId != callerPlayerId)
            throw new OccurrenceHostForbiddenException(occurrence.Id, callerPlayerId);
    }

    /// <summary>
    /// Applies a host lifecycle step. Ended rows are never resurrected (AssignmentEnded); any
    /// other step not allowed by <see cref="RosterState.TargetStatusFor"/> is
    /// AssignmentTransitionInvalid. NoShow stops holding supply, so the spot reopens in the same
    /// commit; the row is kept with exit reason NoShow.
    /// </summary>
    private static void ApplyTransition(RosterAssignment assignment, RosterAssignmentTransition transition, DateTime nowUtc)
    {
        if (!RosterState.IsSupply(assignment.Status))
            throw new RosterConflictException(RosterConflictCodes.AssignmentEnded, AssignmentEndedMessage);

        var target = RosterState.TargetStatusFor(assignment.Status, transition)
            ?? throw new RosterConflictException(
                RosterConflictCodes.AssignmentTransitionInvalid,
                $"A roster assignment that is {assignment.Status} cannot be {TransitionVerb(transition)}.");

        assignment.Status = target;
        switch (target)
        {
            case RosterAssignmentStatus.CheckedIn:
                assignment.CheckedInAtUtc = nowUtc;
                break;
            case RosterAssignmentStatus.Active:
                assignment.ActivatedAtUtc = nowUtc;
                break;
            case RosterAssignmentStatus.NoShow:
                assignment.ExitReason = RosterExitReason.NoShow;
                assignment.DepartedAtUtc = nowUtc;
                break;
        }
    }

    private static string TransitionVerb(RosterAssignmentTransition transition) => transition switch
    {
        RosterAssignmentTransition.CheckIn => "checked in",
        RosterAssignmentTransition.Activate => "activated",
        RosterAssignmentTransition.NoShow => "marked as a no-show",
        _ => transition.ToString()
    };

    /// <summary>
    /// Ends a live assignment: Reserved/Confirmed become Cancelled (never participated),
    /// CheckedIn/Active become Departed. <c>DepartedAtUtc</c> records the exit time in both cases.
    /// </summary>
    private static void End(RosterAssignment assignment, RosterExitReason exitReason, DateTime nowUtc)
    {
        if (!RosterState.IsSupply(assignment.Status))
            throw new RosterConflictException(RosterConflictCodes.AssignmentEnded, AssignmentEndedMessage);

        assignment.Status = RosterState.ExitStatusFor(assignment.Status);
        assignment.ExitReason = exitReason;
        assignment.DepartedAtUtc = nowUtc;
    }

    /// <summary>
    /// The single capacity-consuming write, shared by host assignment and player self-claim.
    /// Callers have already validated the occurrence, player, requirement and replacement.
    /// </summary>
    private async Task<RosterAssignment> AllocateAsync(
        Guid occurrenceId,
        Guid playerId,
        RosterRequirement? requirement,
        string? roleCode,
        string? sourceRoleLabel,
        RosterAssignmentStatus status,
        RosterAssignmentSource source,
        Guid? replacedAssignmentId,
        CancellationToken cancellationToken)
    {
        var supplyStatuses = RosterState.SupplyStatuses;
        if (await _context.RosterAssignments.AnyAsync(
                a => a.OccurrenceId == occurrenceId && a.PlayerId == playerId && supplyStatuses.Contains(a.Status),
                cancellationToken))
        {
            throw AlreadyParticipating();
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
                throw new RosterConflictException(RosterConflictCodes.RequirementFull, RequirementFilledMessage);

            _context.Entry(requirement).Property(r => r.RequiredCount).IsModified = true;
        }

        var assignment = new RosterAssignment
        {
            Id = Guid.NewGuid(),
            OccurrenceId = occurrenceId,
            PlayerId = playerId,
            RequirementId = requirement?.Id,
            RoleCode = roleCode,
            SourceRoleLabel = sourceRoleLabel,
            Status = status,
            Source = source,
            ReplacedAssignmentId = replacedAssignmentId
        };
        StampStatusTimestamp(assignment, _timeProvider.GetUtcNow().UtcDateTime);

        _context.RosterAssignments.Add(assignment);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Nothing was saved. Classify only (no retry): when the competing commit took the
            // last open quantity this is a deterministic RequirementFull; otherwise capacity is
            // still open and the caller may retry.
            _context.ChangeTracker.Clear();
            if (requirement is not null && await IsRequirementFullAsync(requirement.Id, cancellationToken))
                throw new RosterConflictException(RosterConflictCodes.RequirementFull, RequirementFilledMessage, ex);

            throw new RosterConflictException(RosterConflictCodes.RosterChanged, RequirementChangedMessage, ex);
        }
        catch (DbUpdateException ex) when (RosterConflictClassifier.IsDuplicateSupplyAssignment(ex))
        {
            throw AlreadyParticipating(ex);
        }

        return assignment;
    }

    private async Task<bool> IsRequirementFullAsync(Guid requirementId, CancellationToken cancellationToken)
    {
        var supplyStatuses = RosterState.SupplyStatuses;
        var state = await _context.RosterRequirements
            .AsNoTracking()
            .Where(r => r.Id == requirementId)
            .Select(r => new
            {
                r.RequiredCount,
                Supply = _context.RosterAssignments.Count(a => a.RequirementId == r.Id && supplyStatuses.Contains(a.Status))
            })
            .FirstOrDefaultAsync(cancellationToken);
        return state is not null && RosterState.OpenQuantity(state.RequiredCount, state.Supply) == 0;
    }

    /// <summary>
    /// The holder ends their own live assignment without deleting it (see <see cref="End"/>).
    /// Open quantity reopens in the same commit because supply is derived from status. The
    /// assignment's RowVersion guards against a concurrent transition.
    /// </summary>
    private async Task<RosterAssignmentDto> EndAssignmentAsync(
        Guid occurrenceId,
        Guid assignmentId,
        RosterExitReason exitReason,
        Guid requiredHolderId,
        CancellationToken cancellationToken)
    {
        var occurrenceStatus = await LoadOccurrenceStatusAsync(occurrenceId, cancellationToken);

        var assignment = await _context.RosterAssignments
            .FirstOrDefaultAsync(a => a.Id == assignmentId && a.OccurrenceId == occurrenceId, cancellationToken)
            ?? throw new RosterAssignmentNotFoundException(assignmentId);

        if (assignment.PlayerId != requiredHolderId)
            throw new RosterAssignmentForbiddenException(assignmentId, requiredHolderId);

        EnsureOpen(occurrenceStatus);

        End(assignment, exitReason, _timeProvider.GetUtcNow().UtcDateTime);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _context.ChangeTracker.Clear();
            var current = await _context.RosterAssignments
                .Where(a => a.Id == assignmentId)
                .Select(a => (RosterAssignmentStatus?)a.Status)
                .FirstOrDefaultAsync(cancellationToken);
            if (current is { } status && !RosterState.IsSupply(status))
                throw new RosterConflictException(RosterConflictCodes.AssignmentEnded, AssignmentEndedMessage, ex);

            throw new RosterConflictException(RosterConflictCodes.RosterChanged, AssignmentChangedMessage, ex);
        }

        var player = await LoadPublicPlayerAsync(assignment.PlayerId, cancellationToken);
        return MapToDto(assignment, player?.Username ?? string.Empty, player?.DisplayName);
    }

    private static RosterClaimResult ExistingClaimOrConflict(RosterAssignmentDto existing, Guid requirementId)
    {
        if (existing.RequirementId == requirementId)
            return new RosterClaimResult(existing, Created: false);

        throw AlreadyParticipating();
    }

    private static RosterConflictException AlreadyParticipating(Exception? innerException = null) =>
        new(RosterConflictCodes.AlreadyParticipating, DuplicateSupplyAssignmentMessage, innerException);

    private async Task<RosterAssignmentDto?> FindLiveAssignmentAsync(Guid occurrenceId, Guid playerId, CancellationToken cancellationToken)
    {
        var supplyStatuses = RosterState.SupplyStatuses;
        var existing = await ProjectAssignments(_context.RosterAssignments.Where(
                a => a.OccurrenceId == occurrenceId && a.PlayerId == playerId && supplyStatuses.Contains(a.Status)))
            .FirstOrDefaultAsync(cancellationToken);
        return existing is null ? null : MarkUtc(existing);
    }

    /// <summary>
    /// A replaced assignment must belong to this occurrence and no longer hold supply. When
    /// <paramref name="requiredRequirementId"/> is given (self-claim) it must also be on that
    /// requirement. The new row's id is server-generated, so it can never replace itself.
    /// </summary>
    private async Task EnsureReplaceableAsync(Guid occurrenceId, Guid replacedId, Guid? requiredRequirementId, CancellationToken cancellationToken)
    {
        var replaced = await _context.RosterAssignments
            .AsNoTracking()
            .Where(a => a.Id == replacedId && a.OccurrenceId == occurrenceId)
            .Select(a => new { a.Status, a.RequirementId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new ArgumentException("The replaced assignment does not reference a roster assignment of this event.");

        if (requiredRequirementId is { } requirementId && replaced.RequirementId != requirementId)
            throw new ArgumentException("The replaced assignment must be on the same roster requirement.");

        if (RosterState.IsSupply(replaced.Status))
        {
            throw new RosterConflictException(
                RosterConflictCodes.ReplacedAssignmentStillActive,
                "The replaced assignment still holds a roster spot; it must have departed, been a no-show or been cancelled.");
        }
    }

    /// <summary>Tracked: its RowVersion guards the capacity check in <see cref="AllocateAsync"/>.</summary>
    private Task<RosterRequirement?> LoadTrackedRequirementAsync(Guid occurrenceId, Guid requirementId, CancellationToken cancellationToken) =>
        _context.RosterRequirements.FirstOrDefaultAsync(r => r.Id == requirementId && r.OccurrenceId == occurrenceId, cancellationToken);

    private async Task<(string Username, string? DisplayName)?> LoadPublicPlayerAsync(Guid playerId, CancellationToken cancellationToken)
    {
        var player = await _context.Players
            .AsNoTracking()
            .Where(p => p.Id == playerId)
            .Select(p => new { p.Username, p.DisplayName })
            .FirstOrDefaultAsync(cancellationToken);
        return player is null ? null : (player.Username, player.DisplayName);
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

    private async Task EnsureOpenOccurrenceAsync(Guid occurrenceId, CancellationToken cancellationToken) =>
        EnsureOpen(await LoadOccurrenceStatusAsync(occurrenceId, cancellationToken));

    private async Task<EventStatus> LoadOccurrenceStatusAsync(Guid occurrenceId, CancellationToken cancellationToken)
    {
        return await _context.Events
            .Where(e => e.Id == occurrenceId)
            .Select(e => (EventStatus?)e.Status)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new EventOccurrenceNotFoundException(occurrenceId);
    }

    private static void EnsureOpen(EventStatus occurrenceStatus)
    {
        if (!RosterState.AcceptsNewRosterMutations(occurrenceStatus))
            throw new RosterConflictException(RosterConflictCodes.OccurrenceClosed, ClosedOccurrenceMessage);
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
