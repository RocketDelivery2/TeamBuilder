using Microsoft.EntityFrameworkCore;
using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Infrastructure.Data;

public class TeamBuilderDbContext : DbContext
{
    public TeamBuilderDbContext(DbContextOptions<TeamBuilderDbContext> options) : base(options)
    {
    }

    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Player> Players => Set<Player>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<TeamEvent> Events => Set<TeamEvent>();
    public DbSet<RosterEntry> RosterEntries => Set<RosterEntry>();
    public DbSet<JoinRequest> JoinRequests => Set<JoinRequest>();
    public DbSet<RosterImport> RosterImports => Set<RosterImport>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TeamBuilderDbContext).Assembly);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateTimestamps();
        InitializeRowVersionForInMemory();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void UpdateTimestamps()
    {
        var entries = ChangeTracker.Entries<BaseEntity>();

        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAtUtc = DateTime.UtcNow;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAtUtc = DateTime.UtcNow;
            }
        }
    }

    private void InitializeRowVersionForInMemory()
    {
        // For InMemory provider, initialize RowVersion if null or empty
        // SQL Server provider will handle this automatically via IsRowVersion()
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            var entries = ChangeTracker.Entries<BaseEntity>();

            foreach (var entry in entries)
            {
                if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
                {
                    if (entry.Entity.RowVersion == null || entry.Entity.RowVersion.Length == 0)
                    {
                        // Generate a simple timestamp-based rowversion for InMemory
                        entry.Entity.RowVersion = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
                    }
                }
            }
        }
    }
}
