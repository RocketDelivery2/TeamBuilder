using System.Text;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Infrastructure.Services;

/// <summary>
/// The teambuilder.roster.v1 read model. Every query filters on the host first, so a game
/// hosted by anyone else is indistinguishable from a missing one. Roster totals come from
/// <see cref="RosterState.Compute"/>, the same arithmetic the app uses. The host is entitled to
/// the full venue (see <see cref="VenuePrivacy"/>), and participants carry public profile
/// fields only.
/// </summary>
public class IntegrationRosterService : IIntegrationRosterService
{
    private readonly TeamBuilderDbContext _context;
    private readonly TimeProvider _timeProvider;

    public IntegrationRosterService(TeamBuilderDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<PaginatedResult<IntegrationOccurrenceDto>> ListHostedOccurrencesAsync(
        Guid hostPlayerId, DateTime fromUtc, DateTime toUtc, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = _context.Events
            .AsNoTracking()
            .Where(e => e.HostId == hostPlayerId && e.ScheduledStartUtc >= fromUtc && e.ScheduledStartUtc < toUtc);

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(e => e.ScheduledStartUtc)
            .ThenBy(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(OccurrenceRowProjection)
            .ToListAsync(cancellationToken);

        var snapshots = await ComputeSnapshotsAsync(rows.Select(r => r.Id).ToList(), cancellationToken);
        return new PaginatedResult<IntegrationOccurrenceDto>
        {
            Items = rows.Select(r => ToOccurrence(r, snapshots[r.Id])).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<IntegrationRosterDto?> GetRosterAsync(Guid hostPlayerId, Guid occurrenceId, CancellationToken cancellationToken = default)
    {
        var row = await _context.Events
            .AsNoTracking()
            .Where(e => e.Id == occurrenceId && e.HostId == hostPlayerId)
            .Select(OccurrenceRowProjection)
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
            return null;

        var requirements = await _context.RosterRequirements
            .AsNoTracking()
            .Where(r => r.OccurrenceId == occurrenceId)
            .OrderBy(r => r.CreatedAtUtc)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);
        var supplyStatuses = RosterState.SupplyStatuses;
        var participants = await _context.RosterAssignments
            .AsNoTracking()
            .Where(a => a.OccurrenceId == occurrenceId && supplyStatuses.Contains(a.Status))
            .OrderBy(a => a.CreatedAtUtc)
            .ThenBy(a => a.Id)
            .Select(a => new
            {
                a.Id,
                a.PlayerId,
                a.Player.Username,
                a.Player.DisplayName,
                a.RequirementId,
                a.RoleCode,
                a.Status,
                a.Source,
                a.ReservedAtUtc,
                a.ConfirmedAtUtc,
                a.CheckedInAtUtc,
                a.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        var snapshot = RosterState.Compute(
            occurrenceId,
            requirements.Select(r => (r.Id, r.RequiredCount)),
            participants.Select(p => (p.RequirementId, p.Status)));

        return new IntegrationRosterDto
        {
            GeneratedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
            Occurrence = ToOccurrence(row, snapshot),
            Venue = row.Venue is { } venue ? ToVenue(venue) : null,
            Requirements = requirements.Select(r =>
            {
                var supply = snapshot.Requirements[r.Id];
                return new IntegrationRequirementDto
                {
                    Id = r.Id,
                    RoleCode = r.RoleCode,
                    DisplayPosition = r.DisplayPosition,
                    SourceRoleLabel = r.SourceRoleLabel,
                    RequiredCount = r.RequiredCount,
                    SupplyCount = supply.SupplyCount,
                    OpenQuantity = supply.OpenQuantity
                };
            }).ToList(),
            Participants = participants.Select(p => new IntegrationParticipantDto
            {
                AssignmentId = p.Id,
                PlayerId = p.PlayerId,
                Username = p.Username,
                DisplayName = p.DisplayName,
                RequirementId = p.RequirementId,
                RoleCode = p.RoleCode,
                Status = ContractCode(p.Status),
                Source = ContractCode(p.Source),
                JoinedAtUtc = Utc(p.ReservedAtUtc ?? p.CreatedAtUtc),
                ConfirmedAtUtc = Utc(p.ConfirmedAtUtc),
                CheckedInAtUtc = Utc(p.CheckedInAtUtc)
            }).ToList()
        };
    }

    /// <summary>
    /// The contract form of an enum value: lowercase snake_case of its name
    /// (<c>InProgress</c> becomes <c>in_progress</c>), never its number.
    /// </summary>
    public static string ContractCode<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString();
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(name[i]));
        }
        return builder.ToString();
    }

    private sealed record OccurrenceRow(
        Guid Id,
        string Name,
        string? Category,
        EventStatus Status,
        DateTime ScheduledStartUtc,
        DateTime? ScheduledEndUtc,
        string? SeriesTimeZoneId,
        Guid? SeriesId,
        Guid? TeamId,
        DateTime CreatedAtUtc,
        DateTime? UpdatedAtUtc,
        Venue? Venue);

    private static readonly System.Linq.Expressions.Expression<Func<EventOccurrence, OccurrenceRow>> OccurrenceRowProjection =
        e => new OccurrenceRow(
            e.Id,
            e.Name,
            e.Category,
            e.Status,
            e.ScheduledStartUtc,
            e.ScheduledEndUtc,
            e.Series != null ? e.Series.TimeZoneId : null,
            e.SeriesId,
            e.TeamId,
            e.CreatedAtUtc,
            e.UpdatedAtUtc,
            e.Venue);

    private async Task<Dictionary<Guid, RosterSupplySnapshot>> ComputeSnapshotsAsync(List<Guid> occurrenceIds, CancellationToken cancellationToken)
    {
        var requirements = await _context.RosterRequirements
            .AsNoTracking()
            .Where(r => occurrenceIds.Contains(r.OccurrenceId))
            .Select(r => new { r.OccurrenceId, r.Id, r.RequiredCount })
            .ToListAsync(cancellationToken);
        var supplyStatuses = RosterState.SupplyStatuses;
        var supply = await _context.RosterAssignments
            .AsNoTracking()
            .Where(a => occurrenceIds.Contains(a.OccurrenceId) && supplyStatuses.Contains(a.Status))
            .Select(a => new { a.OccurrenceId, a.RequirementId, a.Status })
            .ToListAsync(cancellationToken);

        var requirementsByOccurrence = requirements.ToLookup(r => r.OccurrenceId);
        var supplyByOccurrence = supply.ToLookup(a => a.OccurrenceId);
        return occurrenceIds.ToDictionary(
            id => id,
            id => RosterState.Compute(
                id,
                requirementsByOccurrence[id].Select(r => (r.Id, r.RequiredCount)),
                supplyByOccurrence[id].Select(a => (a.RequirementId, a.Status))));
    }

    private static IntegrationOccurrenceDto ToOccurrence(OccurrenceRow row, RosterSupplySnapshot snapshot) => new()
    {
        Id = row.Id,
        Name = row.Name,
        ActivityCode = row.Category,
        Status = ContractCode(row.Status),
        StartsAtUtc = Utc(row.ScheduledStartUtc),
        EndsAtUtc = Utc(row.ScheduledEndUtc),
        TimeZoneId = row.Venue?.TimeZoneId ?? row.SeriesTimeZoneId,
        SeriesId = row.SeriesId,
        TeamId = row.TeamId,
        RequiredCount = snapshot.TotalRequiredCount,
        SupplyCount = snapshot.TotalSupplyCount,
        OpenQuantity = snapshot.TotalOpenQuantity,
        IsRosterReady = snapshot.IsRosterReady,
        CreatedAtUtc = Utc(row.CreatedAtUtc),
        UpdatedAtUtc = Utc(row.UpdatedAtUtc)
    };

    // The key acts for the host, who is entitled to the whole venue (VenuePrivacy); Privacy
    // tells the integration not to republish a private address.
    private static IntegrationVenueDto ToVenue(Venue venue) => new()
    {
        Id = venue.Id,
        Name = venue.Name,
        AddressLine1 = venue.AddressLine1,
        AddressLine2 = venue.AddressLine2,
        City = venue.City,
        StateOrProvince = venue.StateOrProvince,
        PostalCode = venue.PostalCode,
        CountryCode = venue.CountryCode,
        Latitude = venue.Latitude,
        Longitude = venue.Longitude,
        TimeZoneId = venue.TimeZoneId,
        Type = ContractCode(venue.VenueType),
        Privacy = ContractCode(venue.PrivacyLevel)
    };

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? Utc(DateTime? value) => value is { } v ? Utc(v) : null;
}
