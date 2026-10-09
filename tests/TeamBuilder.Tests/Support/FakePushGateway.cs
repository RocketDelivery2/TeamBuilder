using System.Collections.Concurrent;
using System.Net;

namespace TeamBuilder.Tests.Support;

/// <summary>
/// A deterministic stand-in for the browser push services (FCM, Mozilla, Apple, WNS): an
/// HttpMessageHandler that records every request and answers per endpoint. Tests never reach a
/// live push service.
/// </summary>
internal sealed class FakePushGateway : HttpMessageHandler
{
    public sealed record Request(string Endpoint, IReadOnlyDictionary<string, string> Headers, byte[] Body, DateTime AtUtc);

    public ConcurrentQueue<Request> Requests { get; } = new();

    /// <summary>Response per request (default 201 Created). May throw to simulate network failure.</summary>
    public Func<Request, HttpStatusCode> Behavior { get; set; } = _ => HttpStatusCode.Created;

    /// <summary>Optional artificial latency per request (deterministic load tests).</summary>
    public TimeSpan Latency { get; set; }

    public IEnumerable<Request> To(string endpoint) => Requests.Where(r => r.Endpoint == endpoint);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
                headers[header.Key] = string.Join(",", header.Value);
        }
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var recorded = new Request(request.RequestUri!.ToString(), headers, body, DateTime.UtcNow);
        Requests.Enqueue(recorded);
        if (Latency > TimeSpan.Zero)
            await Task.Delay(Latency, cancellationToken);
        return new HttpResponseMessage(Behavior(recorded));
    }
}
