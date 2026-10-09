namespace TeamBuilder.Api.Middleware;

/// <summary>
/// Response headers for a JSON API that is never framed or rendered as a page. The web client
/// sets its own Content-Security-Policy where it is hosted (see apps/TeamBuilder.Web/nginx);
/// this policy only covers API responses. Swagger UI (when enabled) needs inline script, so its
/// paths keep the browser defaults.
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        context.Response.OnStarting(() =>
        {
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            if (!context.Request.Path.StartsWithSegments("/swagger"))
            {
                headers.XFrameOptions = "DENY";
                headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
            }
            return Task.CompletedTask;
        });
        return next(context);
    }
}
