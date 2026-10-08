using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Infrastructure.Services;

/// <summary>
/// The first-party Venue API: create and read. There is deliberately no update or delete: a
/// venue may be shared by other hosts' occurrences, so moving or removing it would move or
/// orphan their games. A host who needs a different place creates a new venue.
/// </summary>
public class VenueService : IVenueService
{
    private readonly TeamBuilderDbContext _context;

    public VenueService(TeamBuilderDbContext context)
    {
        _context = context;
    }

    public async Task<VenueDto> CreateAsync(CreateVenueDto createDto, Guid creatorPlayerId, CancellationToken cancellationToken = default)
    {
        if (createDto.VenueType is not { } venueType || createDto.PrivacyLevel is not { } privacyLevel)
            throw new ArgumentException("VenueType and PrivacyLevel are required.");

        var venue = new Venue
        {
            Id = Guid.NewGuid(),
            Name = createDto.Name.Trim(),
            AddressLine1 = Clean(createDto.AddressLine1),
            AddressLine2 = Clean(createDto.AddressLine2),
            City = Clean(createDto.City),
            StateOrProvince = Clean(createDto.StateOrProvince),
            PostalCode = Clean(createDto.PostalCode),
            CountryCode = Clean(createDto.CountryCode)?.ToUpperInvariant(),
            Latitude = venueType == VenueType.Virtual ? null : createDto.Latitude,
            Longitude = venueType == VenueType.Virtual ? null : createDto.Longitude,
            TimeZoneId = createDto.TimeZoneId,
            VenueType = venueType,
            PrivacyLevel = privacyLevel,
            CreatedByPlayerId = creatorPlayerId
        };

        _context.Venues.Add(venue);
        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(venue, creatorPlayerId);
    }

    public async Task<VenueDto?> GetByIdAsync(Guid id, Guid? callerPlayerId, CancellationToken cancellationToken = default)
    {
        var venue = await _context.Venues.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, cancellationToken);
        return venue is null ? null : ToDto(venue, callerPlayerId);
    }

    /// <summary>
    /// Checks that <paramref name="playerId"/> may hold a game at <paramref name="venueId"/>:
    /// the venue exists (else <see cref="VenueNotFoundException"/>) and is not someone else's
    /// Private venue (else <see cref="VenueAttachForbiddenException"/>).
    /// </summary>
    internal static async Task<AttachableVenue> EnsureAttachableAsync(
        TeamBuilderDbContext context,
        Guid venueId,
        Guid playerId,
        CancellationToken cancellationToken)
    {
        var venue = await context.Venues
            .AsNoTracking()
            .Where(v => v.Id == venueId)
            .Select(v => new { v.Name, v.PrivacyLevel, v.CreatedByPlayerId, v.TimeZoneId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new VenueNotFoundException(venueId);

        if (venue.PrivacyLevel != VenuePrivacyLevel.Public && venue.CreatedByPlayerId != playerId)
            throw new VenueAttachForbiddenException(venueId);

        return new AttachableVenue(venue.Name, venue.TimeZoneId);
    }

    internal sealed record AttachableVenue(string Name, string? TimeZoneId);

    private static VenueDto ToDto(Venue venue, Guid? callerPlayerId)
    {
        var isMine = callerPlayerId is not null && venue.CreatedByPlayerId == callerPlayerId;
        var masked = VenuePrivacy.IsMasked(venue.PrivacyLevel, callerIsEntitled: isMine);
        return new VenueDto
        {
            Id = venue.Id,
            Name = venue.Name,
            AddressLine1 = masked ? null : venue.AddressLine1,
            AddressLine2 = masked ? null : venue.AddressLine2,
            City = venue.City,
            StateOrProvince = venue.StateOrProvince,
            PostalCode = masked ? null : venue.PostalCode,
            CountryCode = venue.CountryCode,
            Latitude = masked ? null : venue.Latitude,
            Longitude = masked ? null : venue.Longitude,
            TimeZoneId = venue.TimeZoneId,
            VenueType = venue.VenueType,
            PrivacyLevel = venue.PrivacyLevel,
            IsAddressMasked = masked,
            IsMine = isMine,
            CreatedAtUtc = EventService.AsUtc(venue.CreatedAtUtc)
        };
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
