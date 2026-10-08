using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TeamBuilder.Api.RateLimiting;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Domain;

namespace TeamBuilder.Api.Controllers;

/// <summary>
/// Local discovery: "basketball near me on Wednesday evening". Anonymous callers are welcome; a
/// linked caller also gets their relationship to each game. The search point is transient: it
/// is used for this one query and is never stored or logged (validation messages never echo it).
/// </summary>
[ApiController]
[Route("api/v1/discover")]
[EnableRateLimiting(DiscoveryRateLimitOptions.PolicyName)]
public class DiscoverController : ControllerBase
{
    private readonly IOccurrenceDiscoveryService _discoveryService;
    private readonly ICurrentPlayerResolver _currentPlayerResolver;

    public DiscoverController(IOccurrenceDiscoveryService discoveryService, ICurrentPlayerResolver currentPlayerResolver)
    {
        _discoveryService = discoveryService;
        _currentPlayerResolver = currentPlayerResolver;
    }

    /// <summary>
    /// Live (Planned, Open, InProgress) occurrences at physical venues within
    /// <paramref name="radiusMiles"/> of (<paramref name="lat"/>, <paramref name="lon"/>), whose
    /// start is in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), closest first, then
    /// by start time, then id. Keyset-paged through <c>nextCursor</c>. Virtual venues, venues
    /// without coordinates and occurrences without a venue never match.
    /// </summary>
    /// <param name="lat">Search latitude, -90..90. Required.</param>
    /// <param name="lon">Search longitude, -180..180. Required.</param>
    /// <param name="radiusMiles">Default 15; clients offer 15/25/50; at most 100.</param>
    /// <param name="activity">Category code such as <c>basketball</c>, case-insensitive. Omit for all.</param>
    /// <param name="fromUtc">Default now.</param>
    /// <param name="toUtc">Default fromUtc + 7 days; at most 30 days after fromUtc.</param>
    /// <param name="openOnly">Only games with at least one open spot.</param>
    /// <param name="pageSize">Default 20, 1..50.</param>
    /// <param name="cursor">The previous page's <c>nextCursor</c>, with the same parameters.</param>
    [HttpGet("occurrences")]
    [ProducesResponseType(typeof(DiscoveredOccurrencePageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<DiscoveredOccurrencePageDto>> DiscoverOccurrences(
        [FromQuery] double? lat,
        [FromQuery] double? lon,
        [FromQuery] double? radiusMiles,
        [FromQuery] string? activity,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] bool openOnly = false,
        [FromQuery] int? pageSize = null,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<(string Key, string Message)>();

        if (lat is not { } latitude || !double.IsFinite(latitude) || latitude < -90 || latitude > 90)
            errors.Add(("lat", "lat is required and must be between -90 and 90."));
        if (lon is not { } longitude || !double.IsFinite(longitude) || longitude < -180 || longitude > 180)
            errors.Add(("lon", "lon is required and must be between -180 and 180."));

        var radius = radiusMiles ?? DiscoverOccurrencesQuery.DefaultRadiusMiles;
        if (!double.IsFinite(radius) || radius <= 0 || radius > DiscoverOccurrencesQuery.MaxRadiusMiles)
            errors.Add(("radiusMiles", $"radiusMiles must be greater than 0 and at most {DiscoverOccurrencesQuery.MaxRadiusMiles}."));

        string? normalizedActivity = null;
        if (activity is not null && !ActivityCodes.TryNormalize(activity, out normalizedActivity, out var activityError))
            errors.Add(("activity", activityError));

        var size = pageSize ?? DiscoverOccurrencesQuery.DefaultPageSize;
        if (size < 1 || size > DiscoverOccurrencesQuery.MaxPageSize)
            errors.Add(("pageSize", $"pageSize must be between 1 and {DiscoverOccurrencesQuery.MaxPageSize}."));

        if (errors.Count > 0)
            return ValidationProblem(Problem(errors.ToArray()));

        var query = new DiscoverOccurrencesQuery(
            lat!.Value,
            lon!.Value,
            radius,
            normalizedActivity,
            fromUtc is { } from ? AsUtc(from) : null,
            toUtc is { } to ? AsUtc(to) : null,
            openOnly,
            size,
            cursor);
        var viewerPlayerId = await _currentPlayerResolver.ResolvePlayerIdAsync(cancellationToken);
        try
        {
            return Ok(await _discoveryService.DiscoverAsync(query, viewerPlayerId, cancellationToken));
        }
        catch (DiscoveryValidationException ex)
        {
            return ValidationProblem(Problem([(ex.Field, ex.Message)]));
        }
    }

    /// <summary>
    /// A value with a zone (…Z or an offset) binds as Local and is converted back to UTC; a value
    /// without one is taken as UTC. The client owns turning "Wednesday around 8 PM" into UTC.
    /// </summary>
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static ValidationProblemDetails Problem((string Key, string Message)[] errors) =>
        new(errors.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray()))
        {
            Status = StatusCodes.Status400BadRequest
        };
}
