using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// Bootstraps the API under test with an isolated in-memory database so
/// integration tests never touch a real SQL Server instance.
/// </summary>
public sealed class TeamBuilderWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>Symmetric key used only in tests to sign and validate tokens.</summary>
    internal const string TestSigningKey = "teambuilder-test-signing-key-32ch!";

    /// <summary>Issuer written into test JWTs.</summary>
    internal const string TestIssuer = "teambuilder-test";

    /// <summary>Audience written into test JWTs.</summary>
    internal const string TestAudience = "teambuilder-api";

    private readonly string _databaseName = Guid.NewGuid().ToString();

    /// <summary>
    /// Creates a signed JWT for use in integration tests.
    /// </summary>
    /// <param name="userId">Value written to the <c>sub</c> claim.</param>
    /// <param name="extraClaims">Any additional claims to include.</param>
    internal static string CreateTestJwt(Guid userId, IEnumerable<Claim>? extraClaims = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = userId.ToString(),
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString()
        };
        if (extraClaims is not null)
        {
            foreach (var c in extraClaims)
                claims[c.Type] = c.Value;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = TestIssuer,
            Audience = TestAudience,
            Claims = claims,
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = creds
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Inject test JWT configuration so Program.cs uses the symmetric key path
        // (no OIDC metadata discovery against a real authority).
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = TestSigningKey,
                ["Jwt:Issuer"]    = TestIssuer,
                ["Jwt:Audience"]  = TestAudience,
                ["Jwt:PlayerIdClaim"] = "sub"
            });
        });

        builder.ConfigureServices(services =>
        {
            // Remove every descriptor related to TeamBuilderDbContext so that
            // EF Core does not see two database providers at once.
            var dbDescriptors = services
                .Where(d =>
                    d.ServiceType == typeof(DbContextOptions<TeamBuilderDbContext>) ||
                    d.ServiceType == typeof(DbContextOptions) ||
                    d.ServiceType.FullName?.StartsWith("Microsoft.EntityFrameworkCore") == true &&
                    d.ServiceType.FullName.Contains("TeamBuilderDbContext"))
                .ToList();
            foreach (var d in dbDescriptors)
                services.Remove(d);

            // Remove all health check registrations so the host can start without a real DB.
            // HealthCheckRegistration instances are held inside IConfigureOptions<HealthCheckServiceOptions>;
            // the simplest cross-platform approach is to clear them via post-configure.
            var healthDescriptors = services
                .Where(d => d.ServiceType.FullName?.Contains("SqlServer") == true ||
                            d.ImplementationType?.FullName?.Contains("SqlServer") == true)
                .ToList();
            foreach (var d in healthDescriptors)
                services.Remove(d);

            services.Configure<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>(opts =>
                opts.Registrations.Clear());

            // Register an in-memory database isolated per factory instance.
            services.AddDbContext<TeamBuilderDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));
        });

        builder.UseEnvironment("Development");
    }
}
