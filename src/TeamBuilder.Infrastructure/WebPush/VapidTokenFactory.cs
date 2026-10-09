using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TeamBuilder.Infrastructure.WebPush;

/// <summary>
/// Builds the RFC 8292 <c>Authorization: vapid t=…, k=…</c> header: an ES256 JWT whose audience
/// is the push service origin, valid for 12 hours, cached per origin and renewed an hour
/// before it expires.
/// </summary>
public sealed class VapidTokenFactory : IDisposable
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan RenewBefore = TimeSpan.FromHours(1);

    private readonly VapidKeys _keys;
    private readonly string _subject;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, (string Header, DateTimeOffset ExpiresAt)> _cache = new(StringComparer.Ordinal);

    public VapidTokenFactory(VapidKeys keys, string subject, TimeProvider timeProvider)
    {
        _keys = keys;
        _subject = subject;
        _timeProvider = timeProvider;
    }

    public string PublicKey => _keys.PublicKey;

    /// <summary>The Authorization header value for an endpoint (audience = its scheme://host[:port]).</summary>
    public string AuthorizationFor(Uri endpoint)
    {
        var audience = endpoint.GetLeftPart(UriPartial.Authority);
        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(audience, out var cached) && cached.ExpiresAt - RenewBefore > now)
            return cached.Header;

        var expiresAt = now + TokenLifetime;
        var header = $"vapid t={CreateToken(audience, expiresAt)}, k={_keys.PublicKey}";
        _cache[audience] = (header, expiresAt);
        return header;
    }

    internal string CreateToken(string audience, DateTimeOffset expiresAt)
    {
        var header = Base64Url.Encode("""{"typ":"JWT","alg":"ES256"}"""u8);
        var claims = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["aud"] = audience,
            ["exp"] = expiresAt.ToUnixTimeSeconds(),
            ["sub"] = _subject
        });
        var signingInput = $"{header}.{Base64Url.Encode(Encoding.UTF8.GetBytes(claims))}";
        // ES256 is the raw r||s form (IEEE P1363), .NET's default for ECDsa.
        var signature = _keys.SigningKey.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
        return string.Create(CultureInfo.InvariantCulture, $"{signingInput}.{Base64Url.Encode(signature)}");
    }

    public void Dispose() => _keys.Dispose();
}
