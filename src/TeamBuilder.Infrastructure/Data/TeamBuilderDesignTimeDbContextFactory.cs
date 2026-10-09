using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TeamBuilder.Infrastructure.Data;

/// <summary>
/// Used by the EF Core tools (<c>dotnet ef migrations add/script/bundle</c>,
/// <c>has-pending-model-changes</c>) and by the migration bundle at run time, so neither starts
/// the API host: building a bundle needs no environment configuration, and applying one needs
/// only a connection string. At run time the bundle takes <c>--connection</c>, or else the
/// <c>ConnectionStrings__TeamBuilderSql</c> environment variable (what the migrator container
/// uses). The fallback below is a local developer database and never a deployed one.
/// </summary>
public sealed class TeamBuilderDesignTimeDbContextFactory : IDesignTimeDbContextFactory<TeamBuilderDbContext>
{
    public const string ConnectionStringVariable = "ConnectionStrings__TeamBuilderSql";

    private const string LocalDevelopmentDatabase =
        "Server=(localdb)\\mssqllocaldb;Database=TeamBuilderDev;Trusted_Connection=True;MultipleActiveResultSets=true";

    public TeamBuilderDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseSqlServer(string.IsNullOrWhiteSpace(connectionString) ? LocalDevelopmentDatabase : connectionString)
            .Options;
        return new TeamBuilderDbContext(options);
    }
}
