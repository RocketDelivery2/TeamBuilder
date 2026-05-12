using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// Bootstraps the API under test with an isolated in-memory database so
/// integration tests never touch a real SQL Server instance.
/// </summary>
public sealed class TeamBuilderWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
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
