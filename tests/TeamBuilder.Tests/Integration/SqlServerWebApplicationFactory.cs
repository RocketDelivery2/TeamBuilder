using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// The API under test over a real, already migrated SQL Server database (for example a
/// <c>SqlServerTestDatabase</c>), so HTTP tests observe real transactions, locks, RowVersion
/// checks and unique indexes rather than the in-memory provider. Optional settings are layered
/// over the test defaults (for example to enable Web Push).
/// </summary>
public sealed class SqlServerWebApplicationFactory(string connectionString, IReadOnlyDictionary<string, string?>? settings = null) : TeamBuilderWebApplicationFactory
{
    protected override void ConfigureDatabase(DbContextOptionsBuilder options) =>
        options.UseSqlServer(connectionString);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        if (settings is not null)
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
    }
}
