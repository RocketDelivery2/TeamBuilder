using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace TeamBuilder.Api.Networking;

/// <summary>
/// Which reverse proxies may set the client address (section <c>ForwardedHeaders</c>). Off by
/// default: the connection's own address is used, and X-Forwarded-For is ignored. When
/// enabled, only requests arriving from a listed proxy address or network have their
/// X-Forwarded-For/-Proto applied, and only <see cref="ForwardLimit"/> hops are taken, so a
/// client on the internet cannot pick its own rate-limit partition by sending the header.
/// Enabling it without any proxy is a startup error rather than "trust everyone".
/// </summary>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    public bool Enabled { get; set; }

    /// <summary>Exact proxy addresses (e.g. the QA nginx container's fixed IP).</summary>
    public List<string> KnownProxies { get; set; } = [];

    /// <summary>Proxy networks in CIDR form (e.g. a load balancer subnet).</summary>
    public List<string> KnownNetworks { get; set; } = [];

    /// <summary>Proxy hops to unwind; 1 for a single reverse proxy.</summary>
    public int ForwardLimit { get; set; } = 1;

    /// <summary>Applies the settings to ASP.NET Core's options; throws on invalid configuration.</summary>
    public void Apply(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.None;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        if (!Enabled)
            return;

        if (ForwardLimit is < 1 or > 10)
            throw new OptionsValidationException(SectionName, typeof(ForwardedHeadersSettings), ["ForwardedHeaders:ForwardLimit must be between 1 and 10."]);

        foreach (var proxy in KnownProxies)
        {
            if (!IPAddress.TryParse(proxy?.Trim(), out var address))
                throw new OptionsValidationException(SectionName, typeof(ForwardedHeadersSettings), ["ForwardedHeaders:KnownProxies entries must be IP addresses."]);
            options.KnownProxies.Add(address);
        }

        foreach (var network in KnownNetworks)
        {
            if (!System.Net.IPNetwork.TryParse(network?.Trim(), out var parsed))
                throw new OptionsValidationException(SectionName, typeof(ForwardedHeadersSettings), ["ForwardedHeaders:KnownNetworks entries must be CIDR networks (e.g. 10.0.0.0/8)."]);
            options.KnownIPNetworks.Add(parsed);
        }

        if (options.KnownProxies.Count == 0 && options.KnownIPNetworks.Count == 0)
            throw new OptionsValidationException(SectionName, typeof(ForwardedHeadersSettings), ["ForwardedHeaders is enabled but no KnownProxies or KnownNetworks are configured; refusing to trust every client."]);

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = ForwardLimit;
        options.RequireHeaderSymmetry = false;
    }
}
