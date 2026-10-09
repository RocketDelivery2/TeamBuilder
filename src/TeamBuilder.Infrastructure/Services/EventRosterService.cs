using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;
using TeamBuilder.Application.Outbox;
using TeamBuilder.Domain;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Domain.Outbox;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Outbox;
using TeamBuilder.Infrastructure.Persistence;

namespace TeamBuilder.Infrastructure.Services;

/// <summary>
/// Roster requirements and live assignments. Supply-status <see cref="RosterAssignment"/> rows
/// are the participation authority for roster operations, computed only through
/// <see cref="RosterState.Compute"/>; the legacy <see cref="EventOccurrence.CurrentParticipantCount"/>
/// is neither read nor written here.
/// <para>
/// Every mutation that ends a live assignment (player leave, host remove, host no-show) stages a
/// <c>roster.vacancy.opened.v1</c> outbox message in the same SaveChanges, and therefore the
/// same SQL Server transaction, as the assignment change whenever it actually increases the
/// requirement's open quantity (<see cref="RosterVacancy"/>). Nothing is sent from here: the
/// outbox worker delivers it after commit, and a rolled-back mutation leaves no message.
/// </para>
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
    public const string RequirementIdRequiredMessage =
        "RequirementId is required: every roster assignment fills a roster requirement of this event.";
    public const string RequirementNotOnOccurrenceMessage =
        "RequirementId does not reference a roster requirement of this event.";

    /// <summary>
    /// Attempts of one host-only assignment write. Each concurrency loss re-runs every check on
    /// fresh state; only repeated losses to sibling changes surface as retryable RosterChanged.
    /// </summary>
    public const int MaxHostUpdateAttempts = 5;

    private readonly TeamBuilderDbContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EventRosterService> _logger;

    public EventRosterService(TeamBuilderDbContext context, TimeProvider timeProvider, ILogger<EventRosterService>? logger = null)
    {
        _context = context;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger<EventRosterService>.Instance;
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

    public async Task<RosterRequirementDto> CreateRequirementAsync(
        Guid occurrenceId,
        CreateRosterRequirementDto createDto,
        Guid hostPlayerId,
        CancellationToken cancellationToken = default)
    {
        if (createDto.RequiredCount is not { } requiredCount)
            throw new ArgumentException("RequiredCount is required.");

        var roleCode = NormalizeRoleCode(createDto.RoleCode) ?? RosterRoleCodes.Participant;

        return await WithCommitTimeHostAuthorityAsync(occurrenceId, hostPlayerId, async occurrence =>
        {
            if (await _context.RosterRequirements.AnyAsync(r => r.OccurrenceId == occurrenceId && r.RoleCode == roleCode, cancellationToken))
                throw new RosterConflictException(RosterConflictCodes.DuplicateRequirementRole, DuplicateRequirementMessage);

            var requirement = NewRequirement(occurrenceId, roleCode, requiredCount, createDto.DisplayPosition, createDto.SourceRoleLabel);
            _context.RosterRequirements.Add(requirement);
            TouchForCommitTimeAuthority(occurrence);
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException && RosterConflictClassifier.IsDuplicateRequirementRole(ex))
            {
                throw new RosterConflictException(RosterConflictCodes.DuplicateRequirementRole, DuplicateRequirementMessage, ex);
            }

            // A new requirement has no linked assignments yet.
            return MapToDto(requirement, RosterState.Compute(occurrenceId, [(requirement.Id, requirement.RequiredCount)], []));
        }, cancellationToken);
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
        Guid hostPlayerId,
        CancellationToken cancellationToken = default)
    {
        if (createDto.PlayerId is not { } playerId || playerId == Guid.Empty)
            throw new ArgumentException("PlayerId is required.");

        // Every public assignment fills a requirement of this occurrence, so it always counts
        // toward capacity and readiness. Unlinked supply is never created through the API.
        if (createDto.RequirementId is not { } requirementId || requirementId == Guid.Empty)
            throw new RosterValidationException(RosterValidationCodes.RequirementIdRequired, RequirementIdRequiredMessage);

        var status = createDto.Status ?? RosterAssignmentStatus.Confirmed;
        if (!RosterState.IsSupply(status))
            throw new ArgumentException("Status must be Reserved, Confirmed, CheckedIn or Active for a new assignment.");

        var roleCode = NormalizeRoleCode(createDto.RoleCode);

        return await WithCommitTimeHostAuthorityAsync(occurrenceId, hostPlayerId, async occurrence =>
        {
            // Membership is deliberately not consulted: any existing player may participate.
            var player = await LoadPublicPlayerAsync(playerId, cancellationToken)
                ?? throw new ArgumentException("PlayerId does not reference an existing player.");

            var requirement = await LoadTrackedRequirementAsync(occurrenceId, requirementId, cancellationToken)
                ?? throw new RosterValidationException(RosterValidationCodes.RequirementNotOnOccurrence, RequirementNotOnOccurrenceMessage);

            if (roleCode is not null && !string.Equals(roleCode, requirement.RoleCode, StringComparison.Ordinal))
                throw new ArgumentException($"RoleCode must match the requirement's role code '{requirement.RoleCode}' or be omitted.");

            if (createDto.ReplacedAssignmentId is { } replacedId)
                await EnsureReplaceableAsync(occurrenceId, replacedId, requiredRequirementId: null, cancellationToken);

            var assignment = await StageAllocationAsync(
                occurrenceId,
                playerId,
                requirement,
                requirement.RoleCode,
                createDto.SourceRoleLabel,
                status,
                RosterAssignmentSource.Host,
                createDto.ReplacedAssignmentId,
                cancellationToken);
            TouchForCommitTimeAuthority(occurrence);

            // A concurrency loss (host transfer, status change, a competing fill of the same
            // requirement) propagates to the bounded re-validation, which re-reads everything
            // and turns it into the real outcome: 403, OccurrenceClosed, RequirementFull or success.
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException && RosterConflictClassifier.IsDuplicateSupplyAssignment(ex))
            {
                throw AlreadyParticipating(ex);
            }

            return MapToDto(assignment, player.Username, player.DisplayName);
        }, cancellationToken);
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
        // With includeHosted, an occurrence the caller hosts is listed even without a qualifying
        // assignment. One predicate over Events, so an occurrence the caller both hosts and plays
        // in appears once.
        var includeHosted = query.IncludeHosted;
        var occurrences = _context.Events
            .AsNoTracking()
            .Where(e => mine.Any(a => a.OccurrenceId == e.Id) || (includeHosted && e.HostId == playerId))
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
            // index); otherwise, with includeTerminal, the most recent historical row; none for
            // an organizer-only hosted occurrence.
            var mineHere = myAssignments
                .Where(a => a.OccurrenceId == occurrence.Id)
                .OrderByDescending(a => RosterState.IsSupply(a.Status))
                .ThenByDescending(a => a.CreatedAtUtc)
                .ThenByDescending(a => a.Id)
                .FirstOrDefault();

            var occurrenceRequirements = requirementsByOccurrence[occurrence.Id].ToList();
            var snapshot = RosterState.Compute(
                occurrence.Id,
                occurrenceRequirements.Select(r => (r.Id, r.RequiredCount)),
                supplyByOccurrence[occurrence.Id].Select(a => (a.RequirementId, a.Status)));

            var requirement = mineHere?.RequirementId is { } requirementId
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
                MyAssignmentId = mineHere?.Id,
                MyAssignmentStatus = mineHere?.Status,
                MyRoleCode = mineHere is null ? null : mineHere.RoleCode ?? requirement?.RoleCode,
                MyRequirementId = mineHere?.RequirementId,
                RequiredCount = requirementSupply?.RequiredCount,
                SupplyCount = requirementSupply?.SupplyCount,
                OpenQuantity = requirementSupply?.OpenQuantity,
                IsRosterReady = snapshot.IsRosterReady,
                TotalRequiredCount = snapshot.TotalRequiredCount,
                TotalSupplyCount = snapshot.TotalSupplyCount,
                TotalOpenQuantity = snapshot.TotalOpenQuantity
            });
        }

        var last = page[^1];
        return new PlayerOccurrencePageDto
        {
            Items = items,
            NextCursor = hasMore ? new PlayerOccurrenceCursor(EventService.AsUtc(last.ScheduledStartUtc), last.Id).Encode() : null
        };
    }

    public async Task<OccurrenceDetailDto?> GetOccurrenceDetailAsync(
        Guid occurrenceId,
        Guid? callerPlayerId,
        CancellationToken cancellationToken = default)
    {
        // Query 1 of 3: the occurrence with its host's public fields and display location.
        var occurrence = await _context.Events
            .AsNoTracking()
            .Where(e => e.Id == occurrenceId)
            .Select(e => new
            {
                e.Id,
                e.SeriesId,
                e.TeamId,
                e.Name,
                e.Description,
                e.ScheduledStartUtc,
                e.ScheduledEndUtc,
                e.Status,
                e.Category,
                e.Region,
                e.VenueId,
                Location = e.Venue != null ? e.Venue.Name : e.LegacyLocation,
                Venue = e.Venue == null ? null : new VenuePrivacy.VenueRow(
                    e.Venue.Id,
                    e.Venue.Name,
                    e.Venue.AddressLine1,
                    e.Venue.AddressLine2,
                    e.Venue.City,
                    e.Venue.StateOrProvince,
                    e.Venue.PostalCode,
                    e.Venue.CountryCode,
                    e.Venue.Latitude,
                    e.Venue.Longitude,
                    e.Venue.TimeZoneId,
                    e.Venue.VenueType,
                    e.Venue.PrivacyLevel),
                e.HostId,
                HostUsername = e.Host != null ? e.Host.Username : null,
                HostDisplayName = e.Host != null ? e.Host.DisplayName : null
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (occurrence is null)
            return null;

        // Query 2: requirements. Query 3: live participants only (history stays on the paged
        // assignments route), public player fields only. Query 4, only for a linked caller who
        // holds no spot (the only one who can use "notify me"): their own subscriptions.
        var requirements = await LoadRequirementsAsync(occurrenceId, cancellationToken);
        var supplyStatuses = RosterState.SupplyStatuses;
        var participants = await _context.RosterAssignments
            .AsNoTracking()
            .Where(a => a.OccurrenceId == occurrenceId && supplyStatuses.Contains(a.Status))
            .OrderBy(a => a.CreatedAtUtc)
            .ThenBy(a => a.Id)
            .Select(a => new OccurrenceParticipantDto
            {
                AssignmentId = a.Id,
                PlayerId = a.PlayerId,
                Username = a.Player.Username,
                DisplayName = a.Player.DisplayName,
                RequirementId = a.RequirementId,
                RoleCode = a.RoleCode,
                Status = a.Status
            })
            .ToListAsync(cancellationToken);

        var snapshot = RosterState.Compute(
            occurrenceId,
            requirements.Select(r => (r.Id, r.RequiredCount)),
            participants.Select(p => (p.RequirementId, p.Status)));

        foreach (var participant in participants)
            participant.IsHost = participant.PlayerId == occurrence.HostId;

        var mine = callerPlayerId is { } caller ? participants.FirstOrDefault(p => p.PlayerId == caller) : null;
        var isHost = callerPlayerId is not null && occurrence.HostId == callerPlayerId;

        // A Private venue's exact address is for the people actually going: the host and
        // current participants. Everyone else gets the masked venue.
        var venue = occurrence.Venue is null
            ? null
            : VenuePrivacy.ToOccurrenceVenue(occurrence.Venue, callerIsEntitled: isHost || mine is not null, distanceMeters: null);

        IReadOnlyList<Guid> subscribedRequirementIds = callerPlayerId is { } subscriber && mine is null
            ? await _context.OccurrenceRosterSubscriptions
                .AsNoTracking()
                .Where(s => s.PlayerId == subscriber && s.OccurrenceId == occurrenceId)
                .Select(s => s.RosterRequirementId)
                .ToListAsync(cancellationToken)
            : [];

        return new OccurrenceDetailDto
        {
            OccurrenceId = occurrence.Id,
            SeriesId = occurrence.SeriesId,
            TeamId = occurrence.TeamId,
            Name = occurrence.Name,
            Description = occurrence.Description,
            ScheduledStartUtc = EventService.AsUtc(occurrence.ScheduledStartUtc),
            ScheduledEndUtc = AsUtc(occurrence.ScheduledEndUtc),
            Status = occurrence.Status,
            AcceptsRosterChanges = RosterState.AcceptsNewRosterMutations(occurrence.Status),
            Category = occurrence.Category,
            Region = occurrence.Region,
            VenueId = occurrence.VenueId,
            Location = occurrence.Location,
            Venue = venue,
            HostPlayerId = occurrence.HostId,
            HostUsername = occurrence.HostUsername,
            HostDisplayName = occurrence.HostDisplayName,
            RequiredCount = snapshot.TotalRequiredCount,
            SupplyCount = snapshot.TotalSupplyCount,
            OpenQuantity = snapshot.TotalOpenQuantity,
            IsRosterReady = snapshot.IsRosterReady,
            Requirements = requirements.Select(r => MapToDto(r, snapshot)).ToList(),
            Participants = participants,
            IsHost = isHost,
            MyAssignmentId = mine?.AssignmentId,
            MyAssignmentStatus = mine?.Status,
            MyRequirementId = mine?.RequirementId,
            MySubscribedRequirementIds = subscribedRequirementIds
        };
    }

    /// <summary>
    /// The shared host-only write on one assignment (lifecycle transitions, host removal),
    /// under <see cref="WithCommitTimeHostAuthorityAsync{T}"/>. The assignment's own RowVersion
    /// guards it against a concurrent leave/remove/transition; a loss re-runs every check, which
    /// turns it into AssignmentEnded, AssignmentTransitionInvalid, or success when only a
    /// sibling change on the same occurrence moved its RowVersion.
    /// </summary>
    private Task<RosterAssignmentDto> HostUpdateAssignmentAsync(
        Guid occurrenceId,
        Guid assignmentId,
        Guid hostPlayerId,
        Action<RosterAssignment, DateTime> apply,
        CancellationToken cancellationToken) =>
        WithCommitTimeHostAuthorityAsync(occurrenceId, hostPlayerId, async occurrence =>
        {
            var assignment = await _context.RosterAssignments
                .FirstOrDefaultAsync(a => a.Id == assignmentId && a.OccurrenceId == occurrenceId, cancellationToken)
                ?? throw new RosterAssignmentNotFoundException(assignmentId);

            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var heldSupply = RosterState.IsSupply(assignment.Status);
            apply(assignment, nowUtc);

            // Remove and no-show stop holding supply; check-in and activate do not.
            var vacancy = heldSupply && !RosterState.IsSupply(assignment.Status)
                ? await StageVacancyIfOpenedAsync(assignment, occurrence.ScheduledStartUtc, occurrence.VenueId, nowUtc, cancellationToken)
                : null;

            // One SaveChanges: the occurrence RowVersion UPDATE (commit-time host/status guard),
            // the assignment UPDATE and the outbox INSERT commit or roll back together.
            TouchForCommitTimeAuthority(occurrence);
            await _context.SaveChangesAsync(cancellationToken);
            RecordVacancy(vacancy);

            var player = await LoadPublicPlayerAsync(assignment.PlayerId, cancellationToken);
            return MapToDto(assignment, player?.Username ?? string.Empty, player?.DisplayName);
        }, cancellationToken);

    /// <summary>
    /// Runs one host-only roster write with commit-time host authority. Host and occurrence
    /// state are checked on a tracked read of the occurrence (404, 409 OccurrenceHasNoHost, 403,
    /// 409 OccurrenceClosed); <paramref name="write"/> stages its change, calls
    /// <see cref="TouchForCommitTimeAuthority"/> and saves, so the occurrence row gets a
    /// RowVersion-checked UPDATE in the same transaction as the roster change. A host transfer,
    /// cancellation or status change that commits first therefore makes the save fail instead
    /// of being ignored. On a concurrency loss nothing was saved; the whole operation is re-run
    /// from fresh state (a bounded re-validation, never a blind overwrite) at most
    /// <see cref="MaxHostUpdateAttempts"/> times, after which it is the retryable 409 RosterChanged.
    /// </summary>
    private async Task<T> WithCommitTimeHostAuthorityAsync<T>(
        Guid occurrenceId,
        Guid hostPlayerId,
        Func<EventOccurrence, Task<T>> write,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var occurrence = await _context.Events.FirstOrDefaultAsync(e => e.Id == occurrenceId, cancellationToken)
                ?? throw new EventOccurrenceNotFoundException(occurrenceId);

            EnsureHost(occurrence, hostPlayerId);
            EnsureOpen(occurrence.Status);

            try
            {
                return await write(occurrence);
            }
            catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || RosterConflictClassifier.IsDuplicateOutboxMessage(ex))
            {
                // A duplicate vacancy key is a lost race too: a concurrent leave/remove/no-show of
                // the same assignment committed first (with its own vacancy). SQL Server can
                // report that INSERT before the stale assignment UPDATE's zero row count.
                _context.ChangeTracker.Clear();
                if (attempt >= MaxHostUpdateAttempts)
                    throw new RosterConflictException(RosterConflictCodes.RosterChanged, AssignmentChangedMessage, ex);
            }
        }
    }

    /// <summary>
    /// Commit-time host/status check: no value changes, but the next save UPDATEs the
    /// occurrence row with the RowVersion read when host authority was checked.
    /// </summary>
    private void TouchForCommitTimeAuthority(EventOccurrence occurrence) =>
        _context.Entry(occurrence).Property(e => e.HostId).IsModified = true;

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
    /// The self-claim write: <see cref="StageAllocationAsync"/> then save. A concurrency loss is
    /// classified only (no retry): RequirementFull when the competing commit took the last open
    /// quantity, otherwise the retryable RosterChanged.
    /// </summary>
    private async Task<RosterAssignment> AllocateAsync(
        Guid occurrenceId,
        Guid playerId,
        RosterRequirement requirement,
        string? roleCode,
        string? sourceRoleLabel,
        RosterAssignmentStatus status,
        RosterAssignmentSource source,
        Guid? replacedAssignmentId,
        CancellationToken cancellationToken)
    {
        var assignment = await StageAllocationAsync(
            occurrenceId, playerId, requirement, roleCode, sourceRoleLabel, status, source, replacedAssignmentId, cancellationToken);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _context.ChangeTracker.Clear();
            if (await IsRequirementFullAsync(requirement.Id, cancellationToken))
                throw new RosterConflictException(RosterConflictCodes.RequirementFull, RequirementFilledMessage, ex);

            throw new RosterConflictException(RosterConflictCodes.RosterChanged, RequirementChangedMessage, ex);
        }
        catch (DbUpdateException ex) when (RosterConflictClassifier.IsDuplicateSupplyAssignment(ex))
        {
            throw AlreadyParticipating(ex);
        }

        return assignment;
    }

    /// <summary>
    /// The single capacity-consuming roster invariant, shared by host assignment, player
    /// self-claim and host participation at event creation: no second live assignment for the
    /// player on the occurrence, and no supply above the requirement's RequiredCount. Stages the
    /// new assignment without saving; the caller saves (and classifies a concurrency loss).
    /// The supply count is read after the requirement (and its RowVersion), and an existing
    /// requirement row is always UPDATEd with a RowVersion check in the same transaction as the
    /// INSERT, so two concurrent fills of the last open quantity cannot both commit. A
    /// requirement still being created in the same unit of work has no supply yet.
    /// </summary>
    internal async Task<RosterAssignment> StageAllocationAsync(
        Guid occurrenceId,
        Guid playerId,
        RosterRequirement requirement,
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

        var supply = await _context.RosterAssignments.CountAsync(
            a => a.RequirementId == requirement.Id && supplyStatuses.Contains(a.Status),
            cancellationToken);
        if (RosterState.OpenQuantity(requirement.RequiredCount, supply) == 0)
            throw new RosterConflictException(RosterConflictCodes.RequirementFull, RequirementFilledMessage);

        var requirementEntry = _context.Entry(requirement);
        if (requirementEntry.State != EntityState.Added)
            requirementEntry.Property(r => r.RequiredCount).IsModified = true;

        var assignment = new RosterAssignment
        {
            Id = Guid.NewGuid(),
            OccurrenceId = occurrenceId,
            PlayerId = playerId,
            RequirementId = requirement.Id,
            RoleCode = roleCode,
            SourceRoleLabel = sourceRoleLabel,
            Status = status,
            Source = source,
            ReplacedAssignmentId = replacedAssignmentId
        };
        StampStatusTimestamp(assignment, _timeProvider.GetUtcNow().UtcDateTime);

        _context.RosterAssignments.Add(assignment);
        return assignment;
    }

    /// <summary>A new requirement entity; role code already normalized and count already validated.</summary>
    internal static RosterRequirement NewRequirement(
        Guid occurrenceId,
        string roleCode,
        int requiredCount,
        string? displayPosition,
        string? sourceRoleLabel) => new()
        {
            Id = Guid.NewGuid(),
            OccurrenceId = occurrenceId,
            RoleCode = roleCode,
            DisplayPosition = displayPosition,
            SourceRoleLabel = sourceRoleLabel,
            RequiredCount = requiredCount
        };

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
    /// Open quantity reopens in the same commit because supply is derived from status, and the
    /// vacancy outbox message (if capacity actually opened) is inserted by the same SaveChanges.
    /// The assignment's RowVersion guards against a concurrent transition. A concurrency loss
    /// that left the assignment untouched can only come from the overfilled-requirement guard in
    /// <see cref="StageVacancyIfOpenedAsync"/>, so the leave is re-run on fresh state (bounded).
    /// </summary>
    private async Task<RosterAssignmentDto> EndAssignmentAsync(
        Guid occurrenceId,
        Guid assignmentId,
        RosterExitReason exitReason,
        Guid requiredHolderId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var occurrence = await _context.Events
                .AsNoTracking()
                .Where(e => e.Id == occurrenceId)
                .Select(e => new { e.Status, e.ScheduledStartUtc, e.VenueId })
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new EventOccurrenceNotFoundException(occurrenceId);

            var assignment = await _context.RosterAssignments
                .FirstOrDefaultAsync(a => a.Id == assignmentId && a.OccurrenceId == occurrenceId, cancellationToken)
                ?? throw new RosterAssignmentNotFoundException(assignmentId);

            if (assignment.PlayerId != requiredHolderId)
                throw new RosterAssignmentForbiddenException(assignmentId, requiredHolderId);

            EnsureOpen(occurrence.Status);

            var statusBefore = assignment.Status;
            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            End(assignment, exitReason, nowUtc);
            var vacancy = await StageVacancyIfOpenedAsync(assignment, occurrence.ScheduledStartUtc, occurrence.VenueId, nowUtc, cancellationToken);

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || RosterConflictClassifier.IsDuplicateOutboxMessage(ex))
            {
                // A duplicate vacancy key means a concurrent remove/no-show of this assignment
                // committed first; it is classified like the RowVersion loss it really is.
                _context.ChangeTracker.Clear();
                var current = await _context.RosterAssignments
                    .Where(a => a.Id == assignmentId)
                    .Select(a => (RosterAssignmentStatus?)a.Status)
                    .FirstOrDefaultAsync(cancellationToken);
                if (current is { } status && !RosterState.IsSupply(status))
                    throw new RosterConflictException(RosterConflictCodes.AssignmentEnded, AssignmentEndedMessage, ex);

                if (current == statusBefore && attempt < MaxHostUpdateAttempts)
                    continue;

                throw new RosterConflictException(RosterConflictCodes.RosterChanged, AssignmentChangedMessage, ex);
            }

            RecordVacancy(vacancy);
            var player = await LoadPublicPlayerAsync(assignment.PlayerId, cancellationToken);
            return MapToDto(assignment, player?.Username ?? string.Empty, player?.DisplayName);
        }
    }

    /// <summary>
    /// Stages a <c>roster.vacancy.opened.v1</c> outbox INSERT for <paramref name="assignment"/>,
    /// which has just (in memory, not yet saved) stopped holding supply, when that increases its
    /// requirement's open quantity (<see cref="RosterVacancy.Evaluate"/>). The caller's single
    /// SaveChanges commits it with the roster change. Nothing is staged for an assignment with
    /// no requirement or for an exit reason that is not a vacancy path.
    /// <para>
    /// Supply of the other assignments is read now, without a lock. While the requirement is not
    /// overfilled that is exact enough: every departure opens capacity, whatever else commits
    /// meanwhile. When the others already fill it, whether this departure opens anything
    /// depends on concurrent departures, so the requirement row is also UPDATEd with its
    /// RowVersion (as allocation does): two such departures cannot both commit on the same
    /// stale count, and the loser re-evaluates on fresh state.
    /// </para>
    /// </summary>
    private async Task<RosterVacancyOpenedV1?> StageVacancyIfOpenedAsync(
        RosterAssignment assignment,
        DateTime occurrenceStartUtc,
        Guid? venueId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (assignment.RequirementId is not { } requirementId || RosterVacancy.ReasonFor(assignment.ExitReason) is not { } reason)
            return null;

        var requirement = await LoadTrackedRequirementAsync(assignment.OccurrenceId, requirementId, cancellationToken);
        if (requirement is null)
            return null;

        var supplyStatuses = RosterState.SupplyStatuses;
        var otherSupply = await _context.RosterAssignments.CountAsync(
            a => a.RequirementId == requirementId && a.Id != assignment.Id && supplyStatuses.Contains(a.Status),
            cancellationToken);

        if (otherSupply >= requirement.RequiredCount)
            _context.Entry(requirement).Property(r => r.RequiredCount).IsModified = true;

        var change = RosterVacancy.Evaluate(requirement.RequiredCount, otherSupply);
        if (!change.Opened)
            return null;

        var vacancy = new RosterVacancyOpenedV1
        {
            EventId = Guid.NewGuid(),
            OccurrenceId = assignment.OccurrenceId,
            RosterRequirementId = requirementId,
            RoleCode = requirement.RoleCode,
            VacatedAssignmentId = assignment.Id,
            Reason = reason,
            PreviousOpenQuantity = change.PreviousOpenQuantity,
            OpenQuantity = change.OpenQuantity,
            RequiredCount = requirement.RequiredCount,
            OccurrenceStartUtc = EventService.AsUtc(occurrenceStartUtc),
            VenueId = venueId,
            OccurredAtUtc = nowUtc
        };

        _context.OutboxMessages.Add(new OutboxMessage
        {
            Id = vacancy.EventId,
            Type = RosterVacancyOpenedV1.EventType,
            AggregateId = vacancy.OccurrenceId,
            OccurredAtUtc = nowUtc,
            PayloadJson = vacancy.ToJson(),
            DeduplicationKey = RosterVacancyOpenedV1.DeduplicationKeyFor(assignment.Id),
            Status = OutboxMessageStatus.Pending,
            NextAttemptAtUtc = nowUtc,
            CreatedAtUtc = nowUtc
        });

        return vacancy;
    }

    /// <summary>After the commit only: a vacancy that rolled back is never counted.</summary>
    private void RecordVacancy(RosterVacancyOpenedV1? vacancy)
    {
        if (vacancy is null)
            return;

        RefillTelemetry.VacancyOpened.Add(1, new KeyValuePair<string, object?>("reason", vacancy.Reason.ToString()));
        _logger.LogInformation(
            "VacancyOpened: event {EventId} on occurrence {OccurrenceId} requirement {RequirementId} ({Reason}), open {PreviousOpen} -> {Open} of {Required}.",
            vacancy.EventId, vacancy.OccurrenceId, vacancy.RosterRequirementId, vacancy.Reason, vacancy.PreviousOpenQuantity, vacancy.OpenQuantity, vacancy.RequiredCount);
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

    internal static string? NormalizeRoleCode(string? roleCode)
    {
        if (roleCode is null)
            return null;

        if (!RosterRoleCodes.TryNormalize(roleCode, out var normalized, out var error))
            throw new ArgumentException(error);

        return normalized;
    }

    internal static RosterRequirementDto MapToDto(RosterRequirement requirement, RosterSupplySnapshot snapshot)
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
