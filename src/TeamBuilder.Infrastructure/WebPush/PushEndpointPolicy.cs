namespace TeamBuilder.Infrastructure.WebPush;

/// <summary>
/// Decides whether a subscription endpoint is a push service the API may post to. A browser
/// supplies the URL, so without this check any player could make the server send requests to
/// arbitrary (including internal) addresses. Only absolute https URLs on an allowed push
/// service host (exact or subdomain), on the default port, with no user info, are accepted;
/// <see cref="WebPushOptions.AllowLocalhostEndpoints"/> additionally admits
/// <c>http://localhost</c> in Development.
/// </summary>
public static class PushEndpointPolicy
{
    public static bool IsAllowed(string? endpoint, WebPushOptions options)
    {
        if (string.IsNullOrWhiteSpace(endpoint) ||
            endpoint.Length > Data.Configurations.PushSubscriptionConfiguration.EndpointMaxLength ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        if (options.AllowLocalhostEndpoints && uri.IsLoopback && uri.Host == "localhost" &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return true;
        }

        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.HostNameType != UriHostNameType.Dns)
            return false;

        var host = uri.IdnHost.TrimEnd('.');
        return options.EffectiveAllowedEndpointHosts.Any(allowed =>
        {
            var suffix = allowed.Trim().TrimEnd('.');
            return host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>A short, non-secret service label for logs and metrics ("fcm", "mozilla", …), never the URL.</summary>
    public static string ServiceLabel(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return "unknown";
        var host = uri.Host;
        if (host.EndsWith("googleapis.com", StringComparison.OrdinalIgnoreCase)) return "fcm";
        if (host.EndsWith("mozilla.com", StringComparison.OrdinalIgnoreCase)) return "mozilla";
        if (host.EndsWith("apple.com", StringComparison.OrdinalIgnoreCase)) return "apple";
        if (host.EndsWith("windows.com", StringComparison.OrdinalIgnoreCase)) return "wns";
        return uri.IsLoopback ? "localhost" : "other";
    }
}
