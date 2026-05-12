using System.Diagnostics;

namespace TeamBuilder.Api.Middleware;

/// <summary>
/// Stamps every response with an X-Request-Id correlation header and writes one
/// structured log entry per completed request containing the HTTP method, path,
/// status code, elapsed time, and request ID.
///
/// If the incoming request already carries an X-Request-Id header its value is
/// echoed back unchanged. Otherwise a new short ID is generated from the ASP.NET
/// Core TraceIdentifier.
/// </summary>
internal sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    private const string HeaderName = "X-Request-Id";

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

        var sw = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            sw.Stop();
            logger.LogInformation(
                "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs}ms — RequestId: {RequestId}",
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                sw.ElapsedMilliseconds,
                requestId);
        }
    }

    private static string ResolveRequestId(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(incoming))
            return incoming;

        // Fall back to ASP.NET Core's built-in trace identifier, which is unique per request.
        return context.TraceIdentifier;
    }
}
