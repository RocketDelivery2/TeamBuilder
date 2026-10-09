using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Outbox;
using TeamBuilder.Infrastructure.Persistence;
using TeamBuilder.Infrastructure.WebPush;

namespace TeamBuilder.Infrastructure.Services;

/// <summary>
/// Registers the caller's browsers for Web Push. A subscription is keyed by its endpoint (one
/// browser installation): registering it again refreshes keys and last-seen, re-activates it,
/// and, when another player signed in on that browser, moves it to the caller (the dispatcher
/// also refuses to deliver an alert to a subscription its player no longer owns). Unregistering
/// deletes the credentials outright. Validation messages never echo the submitted values.
/// </summary>
public class PushSubscriptionService : IPushSubscriptionService
{
    public const string WebPushDisabledMessage = "Browser notifications are not enabled on this server.";

    private readonly TeamBuilderDbContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly WebPushOptions _options;
    private readonly string? _publicKey;

    public PushSubscriptionService(TeamBuilderDbContext context, TimeProvider timeProvider, IOptions<WebPushOptions> options)
    {
        _context = context;
        _timeProvider = timeProvider;
        _options = options.Value;
        _publicKey = _options.Enabled ? _options.VapidPublicKey?.Trim() : null;
    }

    public WebPushConfigDto GetConfig() => new() { Enabled = _options.Enabled, VapidPublicKey = _publicKey };

    public async Task<PushSubscriptionRegistration> RegisterAsync(Guid playerId, RegisterPushSubscriptionDto request, string? userAgent, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException(WebPushDisabledMessage);

        var endpoint = request.Endpoint?.Trim() ?? string.Empty;
        if (!PushEndpointPolicy.IsAllowed(endpoint, _options))
            throw new ArgumentException("The subscription endpoint is not a supported push service URL.");
        if (!Base64Url.TryDecode(request.Keys?.P256dh?.Trim(), out var p256dh) || !WebPushEncryption.IsValidUserAgentPublicKey(p256dh))
            throw new ArgumentException("keys.p256dh must be a base64url P-256 public key.");
        if (!Base64Url.TryDecode(request.Keys?.Auth?.Trim(), out var auth) || auth.Length != WebPushEncryption.AuthSecretLength)
            throw new ArgumentException("keys.auth must be a base64url 16-byte secret.");

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        DateTime? expiresAt = request.ExpirationTime is > 0 and < 253402300799999
            ? DateTimeOffset.FromUnixTimeMilliseconds(request.ExpirationTime.Value).UtcDateTime
            : null;
        var hash = HashEndpoint(endpoint);
        var family = UserAgentFamilyOf(userAgent);

        for (var attempt = 1; ; attempt++)
        {
            var subscription = await _context.PushSubscriptions.FirstOrDefaultAsync(s => s.EndpointHash == hash, cancellationToken);
            var created = subscription is null;
            if (subscription is null)
            {
                subscription = new PushSubscription { Id = Guid.NewGuid(), Endpoint = endpoint, EndpointHash = hash };
                _context.PushSubscriptions.Add(subscription);
            }

            subscription.PlayerId = playerId;
            subscription.P256dh = Base64Url.Encode(p256dh);
            subscription.Auth = Base64Url.Encode(auth);
            subscription.UserAgentFamily = family;
            subscription.LastSeenAtUtc = now;
            subscription.ExpiresAtUtc = expiresAt;
            subscription.IsActive = true;
            subscription.FailureCount = 0;
            subscription.DisabledAtUtc = null;
            subscription.DisabledReason = null;

            if (!string.IsNullOrWhiteSpace(request.PreviousEndpoint) && request.PreviousEndpoint.Trim() != endpoint)
            {
                // The browser rotated its endpoint: the old one is dead, so stop sending to it.
                var previousHash = HashEndpoint(request.PreviousEndpoint.Trim());
                var previous = await _context.PushSubscriptions
                    .FirstOrDefaultAsync(s => s.EndpointHash == previousHash && s.PlayerId == playerId && s.IsActive, cancellationToken);
                if (previous is not null)
                    Disable(previous, PushSubscriptionDisabledReasons.Replaced, now);
            }

            await EnforceDeviceLimitAsync(playerId, subscription.Id, now, cancellationToken);

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (attempt < 3 && (RosterConflictClassifier.IsDuplicatePushEndpoint(ex) || ex is DbUpdateConcurrencyException))
            {
                // A concurrent registration of the same browser won: update its row instead.
                _context.ChangeTracker.Clear();
                continue;
            }

            return new PushSubscriptionRegistration(ToDto(subscription), created);
        }
    }

    public async Task UnregisterAsync(Guid playerId, string endpoint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("endpoint is required.");

        var hash = HashEndpoint(endpoint.Trim());
        var subscription = await _context.PushSubscriptions
            .FirstOrDefaultAsync(s => s.EndpointHash == hash && s.PlayerId == playerId, cancellationToken);
        if (subscription is not null)
            await RemoveAsync(subscription, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid playerId, Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        var subscription = await _context.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Id == subscriptionId && s.PlayerId == playerId, cancellationToken);
        if (subscription is null)
            return false;

        await RemoveAsync(subscription, cancellationToken);
        return true;
    }

    /// <summary>Deletes the credentials. Idempotent: losing a race to a concurrent delete is the same outcome.</summary>
    private async Task RemoveAsync(PushSubscription subscription, CancellationToken cancellationToken)
    {
        _context.PushSubscriptions.Remove(subscription);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
        }
    }

    public async Task<IReadOnlyList<PushSubscriptionDto>> ListAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        var rows = await _context.PushSubscriptions
            .AsNoTracking()
            .Where(s => s.PlayerId == playerId)
            .OrderByDescending(s => s.IsActive)
            .ThenByDescending(s => s.LastSeenAtUtc)
            .Select(s => new PushSubscriptionDto
            {
                Id = s.Id,
                UserAgentFamily = s.UserAgentFamily,
                CreatedAtUtc = s.CreatedAtUtc,
                LastSeenAtUtc = s.LastSeenAtUtc,
                ExpiresAtUtc = s.ExpiresAtUtc,
                IsActive = s.IsActive,
                DisabledAtUtc = s.DisabledAtUtc,
                DisabledReason = s.DisabledReason
            })
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
            Normalize(row);
        return rows;
    }

    /// <summary>Retires the least recently seen active browsers beyond the per-player limit (the current one is kept).</summary>
    private async Task EnforceDeviceLimitAsync(Guid playerId, Guid keepId, DateTime now, CancellationToken cancellationToken)
    {
        var others = await _context.PushSubscriptions
            .Where(s => s.PlayerId == playerId && s.IsActive && s.Id != keepId)
            .OrderByDescending(s => s.LastSeenAtUtc)
            .ToListAsync(cancellationToken);
        // Tracked entities already disabled in this unit of work (a replaced endpoint) do not count.
        var active = others.Where(s => s.IsActive).ToList();
        foreach (var stale in active.Skip(_options.MaxDevicesPerPlayer - 1))
            Disable(stale, PushSubscriptionDisabledReasons.Evicted, now);
    }

    internal static void Disable(PushSubscription subscription, string reason, DateTime now)
    {
        subscription.IsActive = false;
        subscription.DisabledAtUtc = now;
        subscription.DisabledReason = reason;
        RefillTelemetry.PushSubscriptionDisabled.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }

    public static byte[] HashEndpoint(string endpoint) => SHA256.HashData(Encoding.UTF8.GetBytes(endpoint));

    /// <summary>A coarse family only; the full User-Agent string is not stored.</summary>
    public static string UserAgentFamilyOf(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return "Other";
        if (userAgent.Contains("Edg/", StringComparison.Ordinal)) return "Edge";
        if (userAgent.Contains("Firefox/", StringComparison.Ordinal)) return "Firefox";
        if (userAgent.Contains("Chrome/", StringComparison.Ordinal) || userAgent.Contains("Chromium/", StringComparison.Ordinal)) return "Chrome";
        if (userAgent.Contains("Safari/", StringComparison.Ordinal)) return "Safari";
        return "Other";
    }

    private static PushSubscriptionDto ToDto(PushSubscription s) => Normalize(new PushSubscriptionDto
    {
        Id = s.Id,
        UserAgentFamily = s.UserAgentFamily,
        CreatedAtUtc = s.CreatedAtUtc,
        LastSeenAtUtc = s.LastSeenAtUtc,
        ExpiresAtUtc = s.ExpiresAtUtc,
        IsActive = s.IsActive,
        DisabledAtUtc = s.DisabledAtUtc,
        DisabledReason = s.DisabledReason
    });

    private static PushSubscriptionDto Normalize(PushSubscriptionDto dto)
    {
        dto.CreatedAtUtc = EventService.AsUtc(dto.CreatedAtUtc);
        dto.LastSeenAtUtc = EventService.AsUtc(dto.LastSeenAtUtc);
        dto.ExpiresAtUtc = dto.ExpiresAtUtc is { } e ? EventService.AsUtc(e) : null;
        dto.DisabledAtUtc = dto.DisabledAtUtc is { } d ? EventService.AsUtc(d) : null;
        return dto;
    }
}
