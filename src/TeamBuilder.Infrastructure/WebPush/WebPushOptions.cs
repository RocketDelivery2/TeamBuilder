using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace TeamBuilder.Infrastructure.WebPush;

/// <summary>
/// Web Push settings (section <c>WebPush</c>). Off by default: in-app notifications work
/// without it. When <see cref="Enabled"/>, the VAPID key pair and subject are required and
/// checked at startup (<see cref="WebPushOptionsValidator"/>); the private key must come from a
/// secret store or environment variable (<c>WebPush__VapidPrivateKey</c>), never from a
/// committed file. See docs/deployment.md for generating keys.
/// </summary>
public sealed class WebPushOptions
{
    public const string SectionName = "WebPush";

    public bool Enabled { get; set; }

    /// <summary>VAPID contact: a <c>mailto:</c> or <c>https:</c> URI the push services can reach the operator at.</summary>
    public string? Subject { get; set; }

    /// <summary>The application server public key: base64url, 65-byte uncompressed P-256. Public; served to browsers.</summary>
    public string? VapidPublicKey { get; set; }

    /// <summary>The matching private key: base64url, 32-byte P-256 scalar. Secret.</summary>
    public string? VapidPrivateKey { get; set; }

    /// <summary>
    /// Push service hosts a subscription endpoint may point at (exact host or any subdomain).
    /// Anything else is refused at registration, so the API never posts to an arbitrary URL.
    /// </summary>
    public List<string> AllowedEndpointHosts { get; set; } = [];

    /// <summary>
    /// Development only: also accept <c>http://localhost</c> endpoints (a local fake push
    /// gateway for dogfooding). Refused at startup in any other environment.
    /// </summary>
    public bool AllowLocalhostEndpoints { get; set; }

    /// <summary>Most active browsers per player; registering one more retires the least recently seen.</summary>
    [Range(1, 100)]
    public int MaxDevicesPerPlayer { get; set; } = 10;

    /// <summary>How long a push service should keep trying to reach an offline device (the TTL header), and the
    /// age after which an unsent alert is abandoned: an hour-old "spot opened" is noise.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "1.00:00:00")]
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromMinutes(30);

    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Send attempts per delivery for transient failures (429, 5xx, network), the first included.</summary>
    [Range(1, 20)]
    public int MaxAttempts { get; set; } = 4;

    [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(10);

    [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Consecutive failed deliveries after which a subscription is disabled.</summary>
    [Range(1, 100)]
    public int MaxConsecutiveFailures { get; set; } = 5;

    /// <summary>The dispatcher worker; when false, queued deliveries wait (in-app is unaffected).</summary>
    public bool DispatcherEnabled { get; set; } = true;

    [Range(typeof(TimeSpan), "00:00:00.100", "00:10:00")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    [Range(1, 1000)]
    public int BatchSize { get; set; } = 100;

    /// <summary>Concurrent HTTP sends within one batch.</summary>
    [Range(1, 64)]
    public int MaxConcurrency { get; set; } = 8;

    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The well-known browser push services (Chrome/Edge via FCM and WNS, Firefox, Safari).</summary>
    public static readonly IReadOnlyList<string> DefaultAllowedEndpointHosts =
    [
        "fcm.googleapis.com",
        "android.googleapis.com",
        "push.services.mozilla.com",
        "web.push.apple.com",
        "notify.windows.com"
    ];

    /// <summary>Configured hosts, or the defaults when none are configured.</summary>
    public IReadOnlyList<string> EffectiveAllowedEndpointHosts =>
        AllowedEndpointHosts.Count > 0 ? AllowedEndpointHosts : DefaultAllowedEndpointHosts;

    public TimeSpan RetryDelayFor(int attempt)
    {
        var factor = Math.Pow(2, Math.Clamp(attempt - 1, 0, 30));
        return TimeSpan.FromTicks((long)Math.Min(RetryBaseDelay.Ticks * factor, RetryMaxDelay.Ticks));
    }
}

/// <summary>
/// Fails startup when Web Push is enabled without a usable VAPID identity: missing or
/// malformed keys, a public key that does not belong to the private key, or a subject that is
/// not a mailto:/https: URI. Error messages never include key material.
/// </summary>
public sealed class WebPushOptionsValidator(bool isDevelopment) : IValidateOptions<WebPushOptions>
{
    public ValidateOptionsResult Validate(string? name, WebPushOptions options)
    {
        var failures = new List<string>();

        if (options.AllowLocalhostEndpoints && !isDevelopment)
            failures.Add("WebPush:AllowLocalhostEndpoints is only allowed in the Development environment.");

        if (!options.Enabled)
            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);

        if (string.IsNullOrWhiteSpace(options.Subject) ||
            !(options.Subject.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) && options.Subject.Length > "mailto:".Length ||
              Uri.TryCreate(options.Subject, UriKind.Absolute, out var subject) && subject.Scheme == Uri.UriSchemeHttps))
        {
            failures.Add("WebPush:Subject must be a mailto: or https: URI when Web Push is enabled.");
        }

        if (!VapidKeys.TryCreate(options.VapidPublicKey, options.VapidPrivateKey, out var keys, out var keyError))
            failures.Add(keyError!);
        else
            keys!.Dispose();

        foreach (var host in options.EffectiveAllowedEndpointHosts)
        {
            if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) != UriHostNameType.Dns)
                failures.Add("WebPush:AllowedEndpointHosts entries must be DNS host names.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>A validated VAPID key pair (RFC 8292).</summary>
public sealed class VapidKeys : IDisposable
{
    private VapidKeys(ECDsa signingKey, string publicKey)
    {
        SigningKey = signingKey;
        PublicKey = publicKey;
    }

    public ECDsa SigningKey { get; }

    /// <summary>Base64url uncompressed public key, as browsers expect for <c>applicationServerKey</c>.</summary>
    public string PublicKey { get; }

    public static bool TryCreate(string? publicKey, string? privateKey, out VapidKeys? keys, out string? error)
    {
        keys = null;
        error = null;
        if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(privateKey))
        {
            error = "WebPush:VapidPublicKey and WebPush:VapidPrivateKey are required when Web Push is enabled.";
            return false;
        }

        if (!Base64Url.TryDecode(publicKey.Trim(), out var q) || q.Length != 65 || q[0] != 0x04 ||
            !Base64Url.TryDecode(privateKey.Trim(), out var d) || d.Length != 32)
        {
            error = "WebPush VAPID keys must be base64url: a 65-byte uncompressed P-256 public key and a 32-byte private key.";
            return false;
        }

        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = d,
                Q = new ECPoint { X = q[1..33], Y = q[33..] }
            });
        }
        catch (CryptographicException)
        {
            ecdsa.Dispose();
            error = "WebPush VAPID keys are not a matching P-256 key pair.";
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(d);
        }

        keys = new VapidKeys(ecdsa, Base64Url.Encode(q));
        return true;
    }

    /// <summary>A new P-256 key pair, as (public, private) base64url: what <c>outbox</c>'s <c>vapid-keys</c> command prints.</summary>
    public static (string PublicKey, string PrivateKey) Generate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(true);
        var q = new byte[65];
        q[0] = 0x04;
        parameters.Q.X!.CopyTo(q, 1);
        parameters.Q.Y!.CopyTo(q, 33);
        return (Base64Url.Encode(q), Base64Url.Encode(parameters.D));
    }

    public void Dispose() => SigningKey.Dispose();
}
