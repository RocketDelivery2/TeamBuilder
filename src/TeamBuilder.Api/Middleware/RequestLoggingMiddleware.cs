using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TeamBuilder.Api.Middleware;

/// <summary>
/// Stamps every response with an X-Request-Id correlation header and writes one
/// structured log entry per completed request containing the HTTP method, path,
/// status code, elapsed time, and request ID.
///
/// If the incoming request already carries an X-Request-Id header its value is
/// echoed back unchanged. Otherwise a new short ID is generated from the ASP.NET
/// Core TraceIdentifier.
///
/// User-controlled string values (path, request ID) are sanitized before logging
/// to prevent log injection via embedded control characters or newlines, and an incoming
/// request ID is capped at <see cref="MaxRequestIdLength"/> characters.
///
/// Every log entry written while the request runs carries the request ID (as CorrelationId)
/// and the W3C trace ID in its scope (JSON console logs in QA and Production include scopes), so one request can
/// be followed across the API, the outbox and the push dispatcher. Only the path is logged:
/// query strings (which carry discovery search coordinates), headers (bearer tokens) and
/// bodies (push endpoints) never are.
/// </summary>
internal sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    private const string HeaderName = "X-Request-Id";
    internal const int MaxRequestIdLength = 128;

    // Matches any ASCII/Unicode control character (includes CR, LF, TAB, etc.)
    private static readonly Regex ControlChars = new(@"\p{Cc}", RegexOptions.Compiled);

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = ResolveRequestId(context);

        // Stamp the header before the response is written so it is always present,
        // even when the exception handler short-circuits the pipeline.
        context.Response.OnStarting(() =>
        {
            if (!context.Response.Headers.ContainsKey(HeaderName))
                context.Response.Headers[HeaderName] = requestId;
            return Task.CompletedTask;
        });

        var safeMethod = Sanitize(context.Request.Method);
        var safePath = Sanitize(context.Request.Path.ToString());
        var safeRequestId = Sanitize(requestId);

        var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        // ASP.NET Core's hosting scope already carries TraceId/SpanId; this adds the
        // client-visible correlation id (X-Request-Id) to every entry written for the request.
        using var scope = logger.BeginScope("CorrelationId:{CorrelationId}", safeRequestId);

        var sw = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            sw.Stop();
            logger.LogInformation(
                "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs}ms — RequestId: {RequestId} TraceId: {TraceId}",
                safeMethod,
                safePath,
                context.Response.StatusCode,
                sw.ElapsedMilliseconds,
                safeRequestId,
                traceId);
        }
    }

    private static string ResolveRequestId(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(incoming))
            return incoming.Length <= MaxRequestIdLength ? incoming : incoming[..MaxRequestIdLength];

        // Fall back to ASP.NET Core's built-in trace identifier, which is unique per request.
        return context.TraceIdentifier;
    }

    /// <summary>
    /// Strips control characters from a user-supplied string to prevent log injection.
    /// The original value is still echoed in the response header; only the logged form is sanitized.
    /// </summary>
    private static string Sanitize(string? value) =>
        value is null ? string.Empty : ControlChars.Replace(value, "_");
}
