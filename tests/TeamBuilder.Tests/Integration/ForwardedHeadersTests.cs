using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamBuilder.Api.Networking;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// X-Forwarded-For is honoured only from configured proxies: a direct client cannot choose the
/// address its IP-fallback rate limit is keyed on, the QA nginx proxy can report the real
/// client, and enabling the feature without any proxy fails instead of trusting everyone.
/// </summary>
public class ForwardedHeadersTests
{
    private static readonly IPAddress Proxy = IPAddress.Parse("172.29.80.10");
    private static readonly IPAddress Client = IPAddress.Parse("203.0.113.7");

    [Fact]
    public async Task Default_IgnoresTheHeader_FromADirectClient()
    {
        var context = await RunAsync(new ForwardedHeadersSettings(), remote: Client, forwardedFor: "198.51.100.99");
        context.Connection.RemoteIpAddress.Should().Be(Client);
    }

    [Fact]
    public async Task Enabled_TrustsTheKnownProxy_AndTakesTheClientItReports()
    {
        var settings = new ForwardedHeadersSettings { Enabled = true, KnownProxies = [Proxy.ToString()] };
        var context = await RunAsync(settings, remote: Proxy, forwardedFor: Client.ToString(), proto: "https");
        context.Connection.RemoteIpAddress.Should().Be(Client);
        context.Request.Scheme.Should().Be("https");
    }

    [Fact]
    public async Task Enabled_StillIgnoresTheHeader_FromAnyoneElse()
    {
        var settings = new ForwardedHeadersSettings { Enabled = true, KnownProxies = [Proxy.ToString()] };
        var context = await RunAsync(settings, remote: Client, forwardedFor: "198.51.100.99");
        context.Connection.RemoteIpAddress.Should().Be(Client, "a spoofed header from a non-proxy is ignored");
    }

    [Fact]
    public async Task Enabled_TakesOnlyOneHop_SoAPrependedSpoofIsNotTrusted()
    {
        var settings = new ForwardedHeadersSettings { Enabled = true, KnownNetworks = ["172.29.80.0/24"] };
        // The client sent "X-Forwarded-For: 198.51.100.99"; nginx appended the real client.
        var context = await RunAsync(settings, remote: Proxy, forwardedFor: $"198.51.100.99, {Client}");
        context.Connection.RemoteIpAddress.Should().Be(Client);
    }

    [Theory]
    [InlineData(new string[0], new string[0])]
    [InlineData(new[] { "not-an-ip" }, new string[0])]
    [InlineData(new string[0], new[] { "10.0.0.0/99" })]
    public void Enabled_WithoutAValidProxy_FailsAtStartup(string[] proxies, string[] networks)
    {
        var settings = new ForwardedHeadersSettings { Enabled = true, KnownProxies = [.. proxies], KnownNetworks = [.. networks] };
        ((Action)(() => settings.Apply(new ForwardedHeadersOptions()))).Should().Throw<OptionsValidationException>();
    }

    private static async Task<HttpContext> RunAsync(ForwardedHeadersSettings settings, IPAddress remote, string forwardedFor, string? proto = null)
    {
        var options = new ForwardedHeadersOptions();
        settings.Apply(options);
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remote;
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        if (proto is not null)
            context.Request.Headers["X-Forwarded-Proto"] = proto;
        await middleware.Invoke(context);
        return context;
    }
}
