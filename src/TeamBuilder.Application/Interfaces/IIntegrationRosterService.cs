using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;

namespace TeamBuilder.Application.Interfaces;

/// <summary>
/// Read side of the public integration API (teambuilder.roster.v1). Every read is scoped to
/// games hosted by the key's owner; a game hosted by anyone else reads as not found.
/// </summary>
public interface IIntegrationRosterService
{
    /// <summary>The longest window one occurrence listing may span.</summary>
    static readonly TimeSpan MaxWindow = TimeSpan.FromDays(92);

    /// <summary>
    /// Games hosted by <paramref name="hostPlayerId"/> starting in [<paramref name="fromUtc"/>,
    /// <paramref name="toUtc"/>), earliest first, with roster totals.
    /// </summary>
    Task<PaginatedResult<IntegrationOccurrenceDto>> ListHostedOccurrencesAsync(
        Guid hostPlayerId, DateTime fromUtc, DateTime toUtc, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>The roster of one hosted game; null when it does not exist or another player hosts it.</summary>
    Task<IntegrationRosterDto?> GetRosterAsync(Guid hostPlayerId, Guid occurrenceId, CancellationToken cancellationToken = default);
}
