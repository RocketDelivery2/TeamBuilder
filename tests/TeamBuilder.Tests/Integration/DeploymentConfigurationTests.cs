using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TeamBuilder.Api.Hosting;
using TeamBuilder.Api.Networking;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// Deployed environments fail closed: real OIDC only (no developer signing key), exact https
/// CORS origins, an injected connection string, no startup migration. Development and LocalQA
/// keep their conveniences, and an unknown environment name is treated as deployed.
/// </summary>
public sealed class DeploymentConfigurationValidatorTests
{
    private static readonly Dictionary<string, string?> ValidQa = new()
    {
        ["ConnectionStrings:TeamBuilderSql"] = "Server=tcp:qa-sql.example.test,1433;Database=TeamBuilderQA;User Id=app;Password=x;Encrypt=True",
        ["Jwt:Authority"] = "https://login.example.test/tenant/v2.0",
        ["Jwt:Audience"] = "api://teambuilder-qa",
        ["AllowedOrigins"] = "https://qa.teambuilder.example, https://qa-web.teambuilder.example:8443",
    };

    [Theory]
    [InlineData("QA")]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void ValidDeployedConfiguration_Passes(string environment) =>
        Validate(environment, ValidQa).Should().BeEmpty();

    [Fact]
    public void EmptyAllowedOrigins_IsValid_ForASameOriginDeployment() =>
        Validate("QA", With(ValidQa, "AllowedOrigins", "")).Should().BeEmpty();

    [Theory]
    [InlineData("Jwt:SigningKey", "0123456789abcdef0123456789abcdef", "Jwt:SigningKey must not be set")]
    [InlineData("Jwt:Authority", "", "Jwt:Authority")]
    [InlineData("Jwt:Authority", "http://login.example.test/", "Jwt:Authority")]
    [InlineData("Jwt:Authority", "#{Jwt.Authority}", "Jwt:Authority")]
    [InlineData("Jwt:Audience", "", "Jwt:Audience")]
    [InlineData("Jwt:RequireHttpsMetadata", "false", "RequireHttpsMetadata")]
    [InlineData("Database:ApplyMigrationsOnStartup", "true", "migration bundle")]
    [InlineData("ConnectionStrings:TeamBuilderSql", "", "ConnectionStrings:TeamBuilderSql is required")]
    [InlineData("ConnectionStrings:TeamBuilderSql", "Server=#{AzureSql.ServerName};Database=x", "placeholder")]
    [InlineData("ConnectionStrings:TeamBuilderSql", "Server=(localdb)\\mssqllocaldb;Database=TeamBuilder", "LocalDB")]
    [InlineData("AllowedOrigins", "*", "'*' is only allowed in Development")]
    [InlineData("AllowedOrigins", "http://qa.teambuilder.example", "exact https")]
    [InlineData("AllowedOrigins", "https://qa.teambuilder.example/app", "exact https")]
    [InlineData("AllowedOrigins", "https://qa.teambuilder.example/", "exact https")]
    [InlineData("AllowedOrigins", "#{AllowedOrigins}", "exact https")]
    [InlineData("Deployment:DatabaseEnvironment", " ", "Deployment:DatabaseEnvironment")]
    public void UnsafeDeployedConfiguration_IsRejected(string key, string value, string expected)
    {
        var errors = Validate("QA", With(ValidQa, key, value));
        errors.Should().ContainSingle(e => e.Contains(expected), string.Join(" | ", errors));
    }

    [Fact]
    public void LocalQa_AllowsDeveloperTokensAndStartupMigrations_ButNotAWildcardOrigin()
    {
        var local = new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "0123456789abcdef0123456789abcdef",
            ["Database:ApplyMigrationsOnStartup"] = "true",
            ["AllowedOrigins"] = "http://localhost:8080",
        };
        Validate("LocalQA", local).Should().BeEmpty();
        Validate("LocalQA", With(local, "AllowedOrigins", "*")).Should().ContainSingle();
    }

    [Fact]
    public void Development_KeepsItsConveniences() =>
        Validate("Development", new Dictionary<string, string?> { ["AllowedOrigins"] = "*", ["Jwt:SigningKey"] = "dev" }).Should().BeEmpty();

    [Theory]
    [InlineData("Development", false)]
    [InlineData("LocalQA", false)]
    [InlineData("QA", true)]
    [InlineData("Production", true)]
    [InlineData("qa-typo", true)]
    public void UnknownEnvironmentNames_AreTreatedAsDeployed(string environment, bool deployed) =>
        DeploymentEnvironments.IsDeployed(new TestHostEnvironment(environment)).Should().Be(deployed);

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    public void ForwardedHeaders_RefusesToTrustEveryAddress(string network)
    {
        var settings = new ForwardedHeadersSettings { Enabled = true, KnownNetworks = [network] };
        ((Action)(() => settings.Apply(new ForwardedHeadersOptions()))).Should().Throw<OptionsValidationException>()
            .WithMessage("*/0*");
    }

    private static IReadOnlyList<string> Validate(string environment, IDictionary<string, string?> values) =>
        DeploymentConfigurationValidator.Validate(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new TestHostEnvironment(environment));

    private static Dictionary<string, string?> With(Dictionary<string, string?> values, string key, string? value) =>
        new(values) { [key] = value };

    private sealed class TestHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "TeamBuilder.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

/// <summary>
/// HTTP behaviour every environment shares: JSON liveness with build metadata, exact-origin CORS
/// without credentials, and security headers on API responses.
/// </summary>
public sealed class ProductionShapedHttpTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private readonly HttpClient _client;

    public ProductionShapedHttpTests(TeamBuilderWebApplicationFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Liveness_ReturnsJsonWithVersionAndCommit_AndNoStore()
    {
        var response = await _client.GetAsync("/healthz/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
        body.RootElement.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        body.RootElement.GetProperty("commit").GetString().Should().NotBeNull();
        body.RootElement.GetProperty("checks").EnumerateObject().Should().BeEmpty("liveness runs no dependency check");
    }

    [Fact]
    public async Task Cors_AllowsAListedOrigin_WithoutCredentials()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/push/config");
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");

        var response = await _client.SendAsync(request);

        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal("http://localhost:5173");
        response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
    }

    [Fact]
    public async Task Cors_IgnoresAnUnlistedOrigin()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/push/config");
        request.Headers.Add("Origin", "https://evil.example");

        var response = await _client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task ApiResponses_CarrySecurityHeaders()
    {
        var response = await _client.GetAsync("/api/v1/push/config");

        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        response.Headers.GetValues("Referrer-Policy").Should().Equal("no-referrer");
        response.Headers.GetValues("Content-Security-Policy").Single().Should().StartWith("default-src 'none'");
    }

    [Fact]
    public async Task OverlongRequestId_IsTruncated()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz/live");
        request.Headers.Add("X-Request-Id", new string('a', 500));

        var response = await _client.SendAsync(request);

        response.Headers.GetValues("X-Request-Id").Single().Should().HaveLength(128);
    }
}
