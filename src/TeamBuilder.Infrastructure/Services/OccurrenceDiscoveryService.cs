using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Domain;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Data.Configurations;

namespace TeamBuilder.Infrastructure.Services;

/// <summary>
/// Radius discovery of live occurrences at physical venues, on SQL Server <c>geography</c>
/// (SRID 4326). Four round trips per page whatever its size:
/// <list type="number">
/// <item>the page of (occurrence, distance, start) keys: one hand-written statement, because
/// the spatial predicate has to be the <c>SearchLocation.STDistance(@point) &lt;= @radius</c>
/// form that SQL Server can answer from <c>SIX_Venues_SearchLocation</c>, and the domain model
/// deliberately has no spatial types for EF to translate;</item>
/// <item>the page's occurrence and venue fields;</item>
/// <item>the page's requirements;</item>
/// <item>the page's current (supply-status) assignments, never history.</item>
/// </list>
/// The search point is a query parameter only: it is never stored, and nothing here logs it.
/// </summary>
public class OccurrenceDiscoveryService : IOccurrenceDiscoveryService
{
    private readonly TeamBuilderDbContext _context;
    private readonly TimeProvider _timeProvider;

    public OccurrenceDiscoveryService(TeamBuilderDbContext context, TimeProvider? timeProvider = null)
    {
        _context = context;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>One candidate row of the key query.</summary>
    internal sealed class DiscoveryKeyRow
    {
        public Guid OccurrenceId { get; set; }
        public double DistanceMeters { get; set; }
        public DateTime ScheduledStartUtc { get; set; }
    }

    /// <summary>Statuses whose occurrences still accept players: Planned, Open, InProgress.</summary>
    internal static readonly IReadOnlyList<EventStatus> DiscoverableStatuses =
        Enum.GetValues<EventStatus>().Where(RosterState.AcceptsNewRosterMutations).ToList();

    public async Task<DiscoveredOccurrencePageDto> DiscoverAsync(
        DiscoverOccurrencesQuery query,
        Guid? viewerPlayerId,
        CancellationToken cancellationToken = default)
    {
        var (fromUtc, toUtc, cursor) = ResolveWindow(query);
        var searchHash = DiscoveryCursor.HashOf(query, fromUtc, toUtc);
        if (cursor is { } given && !string.Equals(given.SearchHash, searchHash, StringComparison.Ordinal))
            throw new DiscoveryValidationException("cursor", "The cursor belongs to a different search. Start again without a cursor.");

        var (sql, parameters) = BuildKeyQuery(query, fromUtc, toUtc, cursor);

        // Query 1 of 4.
        var keys = await _context.Database
            .SqlQueryRaw<DiscoveryKeyRow>(sql, parameters)
            .ToListAsync(cancellationToken);

        var hasMore = keys.Count > query.PageSize;
        if (hasMore)
            keys.RemoveAt(keys.Count - 1);
        if (keys.Count == 0)
            return new DiscoveredOccurrencePageDto();

        var occurrenceIds = keys.Select(k => k.OccurrenceId).ToList();

        // Query 2: occurrence and venue fields. Public fields only; no host identity at all.
        var occurrences = await _context.Events
            .AsNoTracking()
            .Where(e => occurrenceIds.Contains(e.Id))
            .Select(e => new
            {
                e.Id,
                e.SeriesId,
                e.Name,
                e.Category,
                e.Status,
                e.ScheduledStartUtc,
                e.ScheduledEndUtc,
                e.HostId,
                Venue = new VenuePrivacy.VenueRow(
                    e.Venue!.Id,
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
                    e.Venue.PrivacyLevel)
            })
            .ToDictionaryAsync(e => e.Id, cancellationToken);

        // Query 3: every requirement of the page (several per occurrence is normal).
        var requirements = await _context.RosterRequirements
            .AsNoTracking()
            .Where(r => occurrenceIds.Contains(r.OccurrenceId))
            .Select(r => new { r.Id, r.OccurrenceId, r.RequiredCount })
            .ToListAsync(cancellationToken);

        // Query 4: current consuming assignments only (bounded by capacity), with the player id
        // solely to recognise the viewer.
        var supplyStatuses = RosterState.SupplyStatuses;
        var supply = await _context.RosterAssignments
            .AsNoTracking()
            .Where(a => occurrenceIds.Contains(a.OccurrenceId) && supplyStatuses.Contains(a.Status))
            .Select(a => new { a.OccurrenceId, a.RequirementId, a.Status, a.PlayerId })
            .ToListAsync(cancellationToken);

        var requirementsByOccurrence = requirements.ToLookup(r => r.OccurrenceId);
        var supplyByOccurrence = supply.ToLookup(a => a.OccurrenceId);

        var items = new List<DiscoveredOccurrenceDto>(keys.Count);
        foreach (var key in keys)
        {
            // An occurrence deleted between query 1 and query 2 is simply skipped.
            if (!occurrences.TryGetValue(key.OccurrenceId, out var occurrence))
                continue;

            var snapshot = RosterState.Compute(
                occurrence.Id,
                requirementsByOccurrence[occurrence.Id].Select(r => (r.Id, r.RequiredCount)),
                supplyByOccurrence[occurrence.Id].Select(a => (a.RequirementId, a.Status)));

            var mine = viewerPlayerId is { } viewer
                ? supplyByOccurrence[occurrence.Id].FirstOrDefault(a => a.PlayerId == viewer)
                : null;
            var isHost = viewerPlayerId is not null && occurrence.HostId == viewerPlayerId;

            items.Add(new DiscoveredOccurrenceDto
            {
                OccurrenceId = occurrence.Id,
                SeriesId = occurrence.SeriesId,
                Name = occurrence.Name,
                Category = occurrence.Category,
                Status = occurrence.Status,
                ScheduledStartUtc = EventService.AsUtc(occurrence.ScheduledStartUtc),
                ScheduledEndUtc = occurrence.ScheduledEndUtc is { } end ? EventService.AsUtc(end) : null,
                TimeZoneId = occurrence.Venue.TimeZoneId,
                Venue = VenuePrivacy.ToOccurrenceVenue(occurrence.Venue, callerIsEntitled: isHost || mine is not null, key.DistanceMeters),
                Roster = new DiscoveredRosterDto
                {
                    TotalRequiredCount = snapshot.TotalRequiredCount,
                    // Supply attached to a requirement (legacy unlinked rows are not demand).
                    TotalSupplyCount = snapshot.Requirements.Values.Sum(r => r.SupplyCount),
                    TotalOpenQuantity = snapshot.TotalOpenQuantity,
                    IsRosterReady = snapshot.IsRosterReady,
                    IsFull = snapshot.Requirements.Count > 0 && snapshot.TotalOpenQuantity == 0
                },
                Viewer = viewerPlayerId is null
                    ? null
                    : new DiscoveryViewerDto
                    {
                        IsHost = isHost,
                        IsParticipating = mine is not null,
                        ParticipationStatus = mine?.Status
                    }
            });
        }

        var last = keys[^1];
        return new DiscoveredOccurrencePageDto
        {
            Items = items,
            NextCursor = hasMore
                ? new DiscoveryCursor(last.DistanceMeters, EventService.AsUtc(last.ScheduledStartUtc), last.OccurrenceId, fromUtc, toUtc, searchHash).Encode()
                : null
        };
    }

    /// <summary>
    /// The search window: as supplied, else the cursor's (so a defaulted "from now" window stays
    /// fixed across pages), else from now for <see cref="DiscoverOccurrencesQuery.DefaultRange"/>.
    /// At most <see cref="DiscoverOccurrencesQuery.MaxRange"/> long.
    /// </summary>
    private (DateTime FromUtc, DateTime ToUtc, DiscoveryCursor? Cursor) ResolveWindow(DiscoverOccurrencesQuery query)
    {
        DiscoveryCursor? cursor = null;
        if (query.Cursor is not null)
        {
            cursor = DiscoveryCursor.TryParse(query.Cursor)
                ?? throw new DiscoveryValidationException("cursor", "The cursor is not valid.");
        }

        var fromUtc = query.FromUtc ?? cursor?.FromUtc ?? _timeProvider.GetUtcNow().UtcDateTime;
        var toUtc = query.ToUtc ?? cursor?.ToUtc ?? fromUtc + DiscoverOccurrencesQuery.DefaultRange;
        if (toUtc <= fromUtc)
            throw new DiscoveryValidationException("toUtc", "toUtc must be after fromUtc.");
        if (toUtc - fromUtc > DiscoverOccurrencesQuery.MaxRange)
            throw new DiscoveryValidationException("toUtc", $"The search window must be at most {DiscoverOccurrencesQuery.MaxRange.TotalDays} days.");

        return (fromUtc, toUtc, cursor);
    }

    /// <summary>
    /// The key query and its parameters. Internal so the SQL Server tests can run the exact
    /// statement under <c>SET STATISTICS XML ON</c> and assert that the plan seeks the spatial
    /// index. Every value is a typed parameter; only fixed SQL fragments are concatenated.
    /// </summary>
    internal static (string Sql, SqlParameter[] Parameters) BuildKeyQuery(
        DiscoverOccurrencesQuery query,
        DateTime fromUtc,
        DateTime toUtc,
        DiscoveryCursor? cursor)
    {
        var statuses = string.Join(", ", DiscoverableStatuses.Select(s => (int)s));
        var supplyStatuses = string.Join(", ", RosterState.SupplyStatuses.Select(s => (int)s));
        var venueTable = "[Venues]";
        var location = $"[v].[{VenueConfiguration.SearchLocationColumnName}]";

        var sql = new StringBuilder($"""
            SELECT TOP (@take) [e].[Id] AS [OccurrenceId], [d].[DistanceMeters], [e].[ScheduledStartUtc]
            FROM (
                SELECT [v].[Id], {location}.STDistance(geography::Point(@lat, @lon, 4326)) AS [DistanceMeters]
                FROM {venueTable} AS [v]
                WHERE {location}.STDistance(geography::Point(@lat, @lon, 4326)) <= @radiusMeters
            ) AS [d]
            INNER JOIN [{EventOccurrenceConfiguration.TableName}] AS [e] ON [e].[VenueId] = [d].[Id]
            WHERE [e].[Status] IN ({statuses})
              AND [e].[ScheduledStartUtc] >= @fromUtc
              AND [e].[ScheduledStartUtc] < @toUtc
            """);
        sql.AppendLine();

        var parameters = new List<SqlParameter>
        {
            new("@take", SqlDbType.Int) { Value = query.PageSize + 1 },
            new("@lat", SqlDbType.Float) { Value = query.Latitude },
            new("@lon", SqlDbType.Float) { Value = query.Longitude },
            new("@radiusMeters", SqlDbType.Float) { Value = query.RadiusMiles * VenuePrivacy.MetersPerMile },
            new("@fromUtc", SqlDbType.DateTime2) { Value = fromUtc },
            new("@toUtc", SqlDbType.DateTime2) { Value = toUtc }
        };

        if (query.Activity is { } activity)
        {
            // Category equality (the database collation is case-insensitive); never a LIKE over
            // event names.
            sql.AppendLine("  AND [e].[Category] = @activity");
            parameters.Add(new SqlParameter("@activity", SqlDbType.NVarChar, ActivityCodes.MaxLength) { Value = activity });
        }

        if (query.OpenOnly)
        {
            // TotalOpen > 0 exactly when some requirement has fewer current assignments than it
            // requires.
            sql.AppendLine($"""
                  AND EXISTS (
                      SELECT 1 FROM [RosterRequirements] AS [r]
                      WHERE [r].[OccurrenceId] = [e].[Id]
                        AND [r].[RequiredCount] > (
                            SELECT COUNT(*) FROM [RosterAssignments] AS [a]
                            WHERE [a].[RequirementId] = [r].[Id]
                              AND [a].[OccurrenceId] = [r].[OccurrenceId]
                              AND [a].[Status] IN ({supplyStatuses})))
                """);
        }

        if (cursor is { } after)
        {
            sql.AppendLine("""
                  AND ([d].[DistanceMeters] > @afterDistance
                    OR ([d].[DistanceMeters] = @afterDistance
                      AND ([e].[ScheduledStartUtc] > @afterStart
                        OR ([e].[ScheduledStartUtc] = @afterStart AND [e].[Id] > @afterId))))
                """);
            parameters.Add(new SqlParameter("@afterDistance", SqlDbType.Float) { Value = after.DistanceMeters });
            parameters.Add(new SqlParameter("@afterStart", SqlDbType.DateTime2) { Value = after.ScheduledStartUtc });
            parameters.Add(new SqlParameter("@afterId", SqlDbType.UniqueIdentifier) { Value = after.OccurrenceId });
        }

        sql.Append("ORDER BY [d].[DistanceMeters], [e].[ScheduledStartUtc], [e].[Id]");
        return (sql.ToString(), parameters.ToArray());
    }
}
