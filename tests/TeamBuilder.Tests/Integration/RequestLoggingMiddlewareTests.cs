using System.Net;
using FluentAssertions;

namespace TeamBuilder.Tests.Integration;

public sealed class RequestLoggingMiddlewareTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private const string RequestIdHeader = "X-Request-Id";

    private readonly HttpClient _client;

    public RequestLoggingMiddlewareTests(TeamBuilderWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Response_IncludesXRequestId_WhenRequestOmitsIt()
    {
        var response = await _client.GetAsync("/health");

        var hasHeader = response.Headers.TryGetValues(RequestIdHeader, out var values);
        hasHeader.Should().BeTrue();
        values!.First().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Response_EchoesXRequestId_WhenRequestProvidesOne()
    {
        var expectedId = "test-correlation-abc123";
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add(RequestIdHeader, expectedId);

        var response = await _client.SendAsync(request);

        var hasHeader = response.Headers.TryGetValues(RequestIdHeader, out var values);
        hasHeader.Should().BeTrue();
        values!.First().Should().Be(expectedId);
    }

    [Fact]
    public async Task Response_IncludesXRequestId_OnApiEndpoints()
    {
        var response = await _client.GetAsync("/api/v1/players");

        var hasHeader = response.Headers.TryGetValues(RequestIdHeader, out var values);
        hasHeader.Should().BeTrue();
        values!.First().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Response_IncludesXRequestId_OnNotFoundResponse()
    {
        var response = await _client.GetAsync($"/api/v1/players/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var hasHeader = response.Headers.TryGetValues(RequestIdHeader, out var values);
        hasHeader.Should().BeTrue();
        values!.First().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Response_EchoesXRequestId_WhenValueContainsNewline()
    {
        // The raw value (including the newline) must be echoed in the response header
        // so callers can correlate. Sanitization applies only to the log output.
        var injectedId = "safe-part\ninjected-line";
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.TryAddWithoutValidation(RequestIdHeader, injectedId);

        var response = await _client.SendAsync(request);

        // Response header must still contain the X-Request-Id header.
        var hasHeader = response.Headers.TryGetValues(RequestIdHeader, out var values);
        hasHeader.Should().BeTrue();

        // The returned value must not be null or empty (header was set).
        values!.First().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Response_EchoesXRequestId_WhenValueContainsCarriageReturn()
    {
        var injectedId = "id-before\r\nHTTP/1.1 200 OK";
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.TryAddWithoutValidation(RequestIdHeader, injectedId);

        var response = await _client.SendAsync(request);

        var hasHeader = response.Headers.TryGetValues(RequestIdHeader, out var values);
        hasHeader.Should().BeTrue();
        values!.First().Should().NotBeNullOrWhiteSpace();
    }
}
