using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TeamBuilder.Api.Operations;
using TeamBuilder.Tests.Application;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// The deployed (QA) API keeps discovery search coordinates out of its logs even when an
/// operator raises <c>Microsoft.AspNetCore</c> to Information while troubleshooting: the hosting
/// "Request starting/finished" entries, which carry the full URL with its query string, stay
/// pinned at Warning in appsettings.json, and outbound HttpClient logging (which would carry push
/// endpoint URLs) stays pinned too.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class DeployedLoggingPrivacySqlServerIntegrationTests(SqlServerContainerFixture fixture)
{
    [Fact]
    public async Task DiscoverySearch_WithAspNetCoreLoggingRaised_NeverLogsTheSearchPoint()
    {
        await using var database = new SqlServerTestDatabase(fixture, "deploy_logs");
        await database.MigrateToAsync();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TeamBuilderSql"] = database.ConnectionString })
            .Build();
        (await DatabaseCommand.RunAsync(["stamp-environment", "QA"], new StringWriter(), configuration)).Should().Be(0);

        var logs = new CapturingLoggerProvider();
        await using var factory = new DeployedEnvironmentWebApplicationFactory(database.ConnectionString,
            settings: new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Information",
                ["Logging:LogLevel:System.Net.Http"] = "Information",
            });
        await using var logged = factory.WithWebHostBuilder(builder => builder.ConfigureLogging(l => l.AddProvider(logs)));
        using var client = logged.CreateClient();

        const string secretLat = "41.873217", secretLon = "-87.627719";
        (await client.GetAsync($"/api/v1/discover/occurrences?lat={secretLat}&lon={secretLon}&radiusMiles=25&activity=basketball"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/v1/discover/occurrences?lat={secretLat}&lon=-187.627719"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        logs.Messages.Should().Contain(m => m.StartsWith("Microsoft.AspNetCore.Routing", StringComparison.Ordinal),
            "Microsoft.AspNetCore Information logging is really on for this test");
        logs.Messages.Should().NotContain(m => m.StartsWith("Microsoft.AspNetCore.Hosting.Diagnostics", StringComparison.Ordinal)
            && m.Contains("Request starting", StringComparison.Ordinal));
        logs.Messages.Should().NotContain(m => m.Contains("41.8732") || m.Contains("87.6277") || m.Contains("lat=") || m.Contains("lon="));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _messages);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue($"{category}: {formatter(state, exception)} {exception}");
        }
    }
}
