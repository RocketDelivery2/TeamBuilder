using System.Net;
using FluentAssertions;

namespace TeamBuilder.Tests.Integration;

public sealed class HealthCheckIntegrationTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private readonly HttpClient _client;

    public HealthCheckIntegrationTests(TeamBuilderWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_ReturnsHealthy_WhenProcessIsRunning()
    {
        var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("Healthy");
    }

    [Fact]
    public async Task HealthReady_ReturnsHealthy_WhenDependencyChecksPass()
    {
        // The factory removes the SqlServer health check and replaces it with
        // an in-memory DB, so no real SQL Server is required for this test.
        // With all "ready"-tagged checks removed, /health/ready also reports Healthy.
        var response = await _client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("Healthy");
    }
}
