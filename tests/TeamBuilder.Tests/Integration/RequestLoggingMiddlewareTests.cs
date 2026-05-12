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
}
