using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace TeamBuilder.Infrastructure.WebPush;

/// <summary>Where and how to deliver one push: the subscription's credentials. Never logged.</summary>
public sealed record WebPushTarget(string Endpoint, string P256dh, string Auth);

/// <param name="Ttl">Seconds the push service may hold the message for an offline device.</param>
/// <param name="Topic">Replaces an undelivered earlier message with the same topic (≤ 32 base64url chars).</param>
public sealed record WebPushMessage(byte[] Payload, int Ttl, string? Topic, string Urgency = "high");

/// <summary>What the push service said, classified (RFC 8030 §5-6, plus vendor practice).</summary>
public enum WebPushOutcome
{
    /// <summary>2xx: accepted by the push service (not proof the device displayed it).</summary>
    Accepted,

    /// <summary>404 / 410: the subscription no longer exists. Disable it.</summary>
    Gone,

    /// <summary>429, 5xx, timeouts and network errors: try again later.</summary>
    Transient,

    /// <summary>Any other 4xx (malformed, too large, unauthorized): retrying the same request will not help.</summary>
    Rejected,

    /// <summary>The stored subscription keys cannot be used to encrypt. Disable it.</summary>
    InvalidSubscription
}

public sealed record WebPushResult(WebPushOutcome Outcome, int? StatusCode, string Category, TimeSpan? RetryAfter = null);

/// <summary>Sends one Web Push message. The production implementation is <see cref="HttpWebPushClient"/>; tests mock the gateway.</summary>
public interface IWebPushClient
{
    Task<WebPushResult> SendAsync(WebPushTarget target, WebPushMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// RFC 8030 delivery over HTTPS: encrypts the payload for the browser (RFC 8291), signs with
/// VAPID (RFC 8292) and posts it to the subscription endpoint. Redirects are not followed
/// (the HttpClient is configured without them) and nothing from the request or response
/// (URL, keys, body) ends up in the result: only the status code and a fixed category.
/// </summary>
public sealed class HttpWebPushClient : IWebPushClient
{
    private readonly HttpClient _http;
    private readonly VapidTokenFactory _vapid;
    private readonly WebPushOptions _options;

    public HttpWebPushClient(HttpClient http, VapidTokenFactory vapid, IOptions<WebPushOptions> options)
    {
        _http = http;
        _vapid = vapid;
        _options = options.Value;
    }

    public async Task<WebPushResult> SendAsync(WebPushTarget target, WebPushMessage message, CancellationToken cancellationToken)
    {
        if (!PushEndpointPolicy.IsAllowed(target.Endpoint, _options))
            return new WebPushResult(WebPushOutcome.InvalidSubscription, null, "EndpointNotAllowed");

        byte[] body;
        try
        {
            body = WebPushEncryption.Encrypt(Base64Url.Decode(target.P256dh), Base64Url.Decode(target.Auth), message.Payload);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            return new WebPushResult(WebPushOutcome.InvalidSubscription, null, "InvalidKeys");
        }

        var endpoint = new Uri(target.Endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("Authorization", _vapid.AuthorizationFor(endpoint));
        request.Headers.TryAddWithoutValidation("TTL", message.Ttl.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Urgency", message.Urgency);
        if (!string.IsNullOrEmpty(message.Topic))
            request.Headers.TryAddWithoutValidation("Topic", message.Topic);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return Classify(response.StatusCode, response.Headers.RetryAfter);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WebPushResult(WebPushOutcome.Transient, null, "Timeout");
        }
        catch (HttpRequestException)
        {
            // The exception text can include the endpoint; only the category is kept.
            return new WebPushResult(WebPushOutcome.Transient, null, "Network");
        }
    }

    public static WebPushResult Classify(HttpStatusCode statusCode, RetryConditionHeaderValue? retryAfter = null)
    {
        var code = (int)statusCode;
        TimeSpan? wait = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return code switch
        {
            >= 200 and < 300 => new WebPushResult(WebPushOutcome.Accepted, code, "Accepted"),
            404 or 410 => new WebPushResult(WebPushOutcome.Gone, code, "Gone"),
            429 => new WebPushResult(WebPushOutcome.Transient, code, "RateLimited", wait),
            >= 500 => new WebPushResult(WebPushOutcome.Transient, code, "ServerError", wait),
            413 => new WebPushResult(WebPushOutcome.Rejected, code, "PayloadTooLarge"),
            401 or 403 => new WebPushResult(WebPushOutcome.Rejected, code, "Unauthorized"),
            _ => new WebPushResult(WebPushOutcome.Rejected, code, "Rejected")
        };
    }
}
