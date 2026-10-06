using Microsoft.EntityFrameworkCore;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// The API under test over a real, already migrated SQL Server database (for example a
/// <c>SqlServerTestDatabase</c>), so HTTP tests observe real transactions, locks, RowVersion
/// checks and unique indexes rather than the in-memory provider.
/// </summary>
public sealed class SqlServerWebApplicationFactory(string connectionString) : TeamBuilderWebApplicationFactory
{
    protected override void ConfigureDatabase(DbContextOptionsBuilder options) =>
        options.UseSqlServer(connectionString);
}
