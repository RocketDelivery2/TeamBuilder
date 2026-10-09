using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using TeamBuilder.Infrastructure.WebPush;
using TeamBuilder.Tests.Support;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// The Web Push protocol pieces in isolation: RFC 8291 encryption against the RFC's own test
/// vector and a browser-side decryption, RFC 8292 VAPID tokens verified with the public key,
/// key and configuration validation, the endpoint allowlist that keeps the API from posting
/// to arbitrary URLs, push service response classification, and the request the client sends.
/// </summary>
public class WebPushProtocolTests
{
    [Fact]
    public void Encryption_ReproducesTheRfc8291Example()
    {
        // RFC 8291 §5 / Appendix A.
        var plaintext = Encoding.UTF8.GetBytes("When I grow up, I want to be a watermelon");
        var uaPublic = Base64Url.Decode("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4");
        var authSecret = Base64Url.Decode("BTBZMqHH6r4Tts7J_aSIgg");
        var salt = Base64Url.Decode("DGv6ra1nlYgDCS1FRnbzlw");
        var asPublic = Base64Url.Decode("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8");
        using var asKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Base64Url.Decode("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw"),
            Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..] }
        });

        var body = WebPushEncryption.Encrypt(uaPublic, authSecret, plaintext, asKey, salt);

        Base64Url.Encode(body).Should().Be(
            "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN");
    }

    [Fact]
    public void Encryption_IsReadableOnlyByTheSubscribedBrowser_AndFreshEachTime()
    {
        using var browser = new SimulatedBrowser("https://fcm.googleapis.com/fcm/send/x");
        using var other = new SimulatedBrowser("https://fcm.googleapis.com/fcm/send/y");
        var plaintext = Encoding.UTF8.GetBytes("""{"title":"Basketball spot opened"}""");

        var first = WebPushEncryption.Encrypt(browser.PublicKey, browser.AuthSecret, plaintext);
        var second = WebPushEncryption.Encrypt(browser.PublicKey, browser.AuthSecret, plaintext);

        browser.Decrypt(first).Should().Equal(plaintext);
        browser.Decrypt(second).Should().Equal(plaintext);
        first.Should().NotEqual(second, "a new salt and ephemeral key per message");
        ((Action)(() => other.Decrypt(first))).Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Encryption_RefusesBadKeysAndOversizedPayloads()
    {
        using var browser = new SimulatedBrowser("https://fcm.googleapis.com/fcm/send/x");
        var notAPoint = new byte[65];
        notAPoint[0] = 0x04;

        WebPushEncryption.IsValidUserAgentPublicKey(browser.PublicKey).Should().BeTrue();
        WebPushEncryption.IsValidUserAgentPublicKey(notAPoint).Should().BeFalse();
        WebPushEncryption.IsValidUserAgentPublicKey(browser.PublicKey[..33]).Should().BeFalse();
        ((Action)(() => WebPushEncryption.Encrypt(browser.PublicKey, new byte[8], [1]))).Should().Throw<ArgumentException>();
        ((Action)(() => WebPushEncryption.Encrypt(browser.PublicKey, browser.AuthSecret, new byte[WebPushEncryption.MaxPlaintextLength + 1]))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void VapidToken_IsAnEs256JwtForThePushServiceOrigin_VerifiableWithThePublicKey()
    {
        var (publicKey, privateKey) = VapidKeys.Generate();
        VapidKeys.TryCreate(publicKey, privateKey, out var keys, out _).Should().BeTrue();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        using var factory = new VapidTokenFactory(keys!, "mailto:qa@teambuilder.test", clock);

        var header = factory.AuthorizationFor(new Uri("https://fcm.googleapis.com/fcm/send/abc:def"));

        header.Should().StartWith("vapid t=").And.EndWith($", k={publicKey}");
        var token = header["vapid t=".Length..header.IndexOf(',')];
        var parts = token.Split('.');
        JsonDocument.Parse(Base64Url.Decode(parts[0])).RootElement.GetProperty("alg").GetString().Should().Be("ES256");
        var claims = JsonDocument.Parse(Base64Url.Decode(parts[1])).RootElement;
        claims.GetProperty("aud").GetString().Should().Be("https://fcm.googleapis.com");
        claims.GetProperty("sub").GetString().Should().Be("mailto:qa@teambuilder.test");
        claims.GetProperty("exp").GetInt64().Should().Be(clock.UtcNow.AddHours(12).ToUnixTimeSeconds());

        var q = Base64Url.Decode(publicKey);
        using var verifier = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = q[1..33], Y = q[33..] } });
        verifier.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Base64Url.Decode(parts[2]), HashAlgorithmName.SHA256).Should().BeTrue();

        factory.AuthorizationFor(new Uri("https://fcm.googleapis.com/fcm/send/other")).Should().Be(header, "cached per origin");
        clock.UtcNow = clock.UtcNow.AddHours(11.5);
        factory.AuthorizationFor(new Uri("https://fcm.googleapis.com/fcm/send/other")).Should().NotBe(header, "renewed before it expires");
    }

    [Theory]
    [InlineData(null, null, "required")]
    [InlineData("not-base64!", "x", "base64url")]
    [InlineData("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4", "AAAA", "base64url")]
    public void VapidKeys_AreValidated_WithoutEchoingThem(string? publicKey, string? privateKey, string expected)
    {
        VapidKeys.TryCreate(publicKey, privateKey, out _, out var error).Should().BeFalse();
        error.Should().Contain(expected);
        if (privateKey is not null)
            error.Should().NotContain(privateKey);
    }

    [Fact]
    public void VapidKeys_FromDifferentPairs_AreRejected()
    {
        var a = VapidKeys.Generate();
        var b = VapidKeys.Generate();
        VapidKeys.TryCreate(a.PublicKey, b.PrivateKey, out _, out var error).Should().BeFalse();
        error.Should().Contain("matching");
    }

    [Fact]
    public void Options_WhenEnabled_RequireAVapidIdentity_AndLocalhostEndpointsOnlyInDevelopment()
    {
        var production = new WebPushOptionsValidator(isDevelopment: false);
        production.Validate(null, new WebPushOptions()).Succeeded.Should().BeTrue("disabled needs nothing");
        production.Validate(null, new WebPushOptions { Enabled = true }).Failed.Should().BeTrue();

        var keys = VapidKeys.Generate();
        var valid = new WebPushOptions { Enabled = true, Subject = "mailto:ops@example.com", VapidPublicKey = keys.PublicKey, VapidPrivateKey = keys.PrivateKey };
        production.Validate(null, valid).Succeeded.Should().BeTrue();
        production.Validate(null, new WebPushOptions { Enabled = true, Subject = "ops@example.com", VapidPublicKey = keys.PublicKey, VapidPrivateKey = keys.PrivateKey })
            .Failures.Should().ContainSingle(f => f.Contains("Subject"));

        production.Validate(null, new WebPushOptions { AllowLocalhostEndpoints = true }).Failed.Should().BeTrue();
        new WebPushOptionsValidator(isDevelopment: true).Validate(null, new WebPushOptions { AllowLocalhostEndpoints = true }).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/abc", true)]
    [InlineData("https://web.push.apple.com/QGz", true)]
    [InlineData("https://wns2-by3p.notify.windows.com/w/?token=abc", true)]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc", false)]
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/abc", false)]
    [InlineData("https://user:pw@fcm.googleapis.com/fcm/send/abc", false)]
    [InlineData("https://evilfcm.googleapis.com.attacker.example/x", false)]
    [InlineData("https://notfcm.googleapis.com.evil/x", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]
    [InlineData("https://localhost/push", false)]
    [InlineData("http://localhost:9999/push", false)]
    [InlineData("https://internal.service/push", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void EndpointPolicy_OnlyAdmitsKnownPushServices(string endpoint, bool allowed) =>
        PushEndpointPolicy.IsAllowed(endpoint, new WebPushOptions()).Should().Be(allowed);

    [Fact]
    public void EndpointPolicy_LocalhostOnlyWithTheDevelopmentSwitch()
    {
        PushEndpointPolicy.IsAllowed("http://localhost:9999/push/x", new WebPushOptions { AllowLocalhostEndpoints = true }).Should().BeTrue();
        PushEndpointPolicy.IsAllowed("http://127.0.0.1:9999/push/x", new WebPushOptions { AllowLocalhostEndpoints = true }).Should().BeFalse();
        PushEndpointPolicy.IsAllowed("https://push.example.net/x", new WebPushOptions { AllowedEndpointHosts = ["push.example.net"] }).Should().BeTrue();
        PushEndpointPolicy.IsAllowed("https://fcm.googleapis.com/x", new WebPushOptions { AllowedEndpointHosts = ["push.example.net"] }).Should().BeFalse("configured hosts replace the defaults");
    }

    [Theory]
    [InlineData(201, WebPushOutcome.Accepted)]
    [InlineData(200, WebPushOutcome.Accepted)]
    [InlineData(404, WebPushOutcome.Gone)]
    [InlineData(410, WebPushOutcome.Gone)]
    [InlineData(429, WebPushOutcome.Transient)]
    [InlineData(500, WebPushOutcome.Transient)]
    [InlineData(503, WebPushOutcome.Transient)]
    [InlineData(400, WebPushOutcome.Rejected)]
    [InlineData(403, WebPushOutcome.Rejected)]
    [InlineData(413, WebPushOutcome.Rejected)]
    public void Responses_AreClassified(int status, WebPushOutcome outcome) =>
        HttpWebPushClient.Classify((HttpStatusCode)status).Outcome.Should().Be(outcome);

    [Fact]
    public void RetryAfter_IsHonoured()
    {
        HttpWebPushClient.Classify(HttpStatusCode.TooManyRequests, new RetryConditionHeaderValue(TimeSpan.FromSeconds(42)))
            .RetryAfter.Should().Be(TimeSpan.FromSeconds(42));
    }

    [Fact]
    public async Task Client_PostsAnEncryptedVapidSignedRequest_AndNeverFollowsOrEchoesTheEndpoint()
    {
        using var browser = new SimulatedBrowser("https://fcm.googleapis.com/fcm/send/device-1");
        var gateway = new FakePushGateway();
        var (client, _) = Client(gateway);
        var payload = Encoding.UTF8.GetBytes("""{"title":"Basketball spot opened"}""");

        var result = await client.SendAsync(new WebPushTarget(browser.Endpoint, browser.P256dh, browser.Auth), new WebPushMessage(payload, 600, "abc123"), CancellationToken.None);

        result.Outcome.Should().Be(WebPushOutcome.Accepted);
        var request = gateway.Requests.Should().ContainSingle().Subject;
        request.Endpoint.Should().Be(browser.Endpoint);
        request.Headers["TTL"].Should().Be("600");
        request.Headers["Urgency"].Should().Be("high");
        request.Headers["Topic"].Should().Be("abc123");
        request.Headers["Content-Encoding"].Should().Be("aes128gcm");
        request.Headers["Authorization"].Should().StartWith("vapid t=");
        browser.Decrypt(request.Body).Should().Equal(payload);
    }

    [Fact]
    public async Task Client_MapsNetworkErrorsToTransient_WithoutTheUrlInTheResult()
    {
        using var browser = new SimulatedBrowser("https://fcm.googleapis.com/fcm/send/secret-token");
        var gateway = new FakePushGateway { Behavior = _ => throw new HttpRequestException("Connection refused (fcm.googleapis.com/fcm/send/secret-token)") };
        var (client, _) = Client(gateway);

        var result = await client.SendAsync(new WebPushTarget(browser.Endpoint, browser.P256dh, browser.Auth), new WebPushMessage([1], 60, null), CancellationToken.None);

        result.Should().Be(new WebPushResult(WebPushOutcome.Transient, null, "Network"));
    }

    [Fact]
    public async Task Client_RefusesAnEndpointOutsideTheAllowlist_WithoutSending()
    {
        using var browser = new SimulatedBrowser("https://169.254.169.254/latest");
        var gateway = new FakePushGateway();
        var (client, _) = Client(gateway);

        var result = await client.SendAsync(new WebPushTarget(browser.Endpoint, browser.P256dh, browser.Auth), new WebPushMessage([1], 60, null), CancellationToken.None);

        result.Outcome.Should().Be(WebPushOutcome.InvalidSubscription);
        gateway.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Payload_IsSparse_AndDeepLinksTheExactGame()
    {
        var occurrenceId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var payload = PushPayload.For(notificationId, occurrenceId, "roster.vacancy", "Basketball spot opened", "A participant spot opened in Wednesday Basketball.", DateTime.UtcNow);

        var json = JsonDocument.Parse(payload.ToUtf8Json()).RootElement;
        json.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["v", "type", "notificationId", "occurrenceId", "title", "body", "url", "tag", "sentAtUtc"]);
        json.GetProperty("url").GetString().Should().Be($"/games/{occurrenceId}?n={notificationId:N}");
    }

    internal static (HttpWebPushClient Client, VapidTokenFactory Vapid) Client(FakePushGateway gateway)
    {
        VapidKeys.TryCreate(WebPushTestKeys.Vapid.PublicKey, WebPushTestKeys.Vapid.PrivateKey, out var keys, out _);
        var vapid = new VapidTokenFactory(keys!, "mailto:qa@teambuilder.test", TimeProvider.System);
        return (new HttpWebPushClient(new HttpClient(gateway), vapid, Options.Create(new WebPushOptions { Enabled = true })), vapid);
    }
}
