using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves, against a real disposable SQL Server (not EF InMemory, which never creates a
/// physical index and can't execute filtered-index semantics), that applying all migrations
/// to an empty database succeeds and installs both filtered unique indexes with their
/// intended filter predicates.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class SqlServerMigrationSchemaIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public SqlServerMigrationSchemaIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "schema");
        await _db.MigrateToAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task MigrateAsync_OnEmptyDatabase_AppliesAllMigrationsSuccessfully()
    {
        await using var context = _db.CreateContext();

        var applied = await context.Database.GetAppliedMigrationsAsync();

        applied.Should().Contain(new[]
        {
            MigrationIds.InitialCreate,
            MigrationIds.EnforceUniqueTeamMembership,
            MigrationIds.EnforceUniquePendingJoinRequest,
            MigrationIds.AddPlayerIdentities
        });
    }

    [Fact]
    public async Task TeamMembersUniqueIndex_IsInstalled_UniqueAndFilteredOnActiveMembership()
    {
        var metadata = await GetIndexMetadataAsync("TeamMembers", "UX_TeamMembers_TeamId_PlayerId");

        metadata.Exists.Should().BeTrue("the migration must create the filtered unique index");
        metadata.IsUnique.Should().BeTrue();
        metadata.FilterDefinition.Should().NotBeNull();
        NormalizeFilter(metadata.FilterDefinition!).Should().Be("[isactive]=1",
            "the filter must be semantically equivalent to [IsActive] = 1");
    }

    [Fact]
    public async Task JoinRequestsUniqueIndex_IsInstalled_UniqueAndFilteredOnPendingStatus()
    {
        var metadata = await GetIndexMetadataAsync("JoinRequests", "UX_JoinRequests_TeamId_PlayerId_Pending");

        metadata.Exists.Should().BeTrue("the migration must create the filtered unique index");
        metadata.IsUnique.Should().BeTrue();
        metadata.FilterDefinition.Should().NotBeNull();
        NormalizeFilter(metadata.FilterDefinition!).Should().Be("[status]=1",
            "the filter must be semantically equivalent to [Status] = 1");
    }

    [Fact]
    public async Task PlayerIdentitiesUniqueIndex_IsInstalled_UniqueAndUnfiltered()
    {
        var metadata = await GetIndexMetadataAsync("PlayerIdentities", "UX_PlayerIdentities_Issuer_Subject");

        metadata.Exists.Should().BeTrue("the migration must create the Issuer+Subject unique index");
        metadata.IsUnique.Should().BeTrue();
        metadata.FilterDefinition.Should().BeNull("every identity row participates in the uniqueness invariant");
    }

    [Fact]
    public async Task PlayerIdentitiesPlayerIdIndex_IsInstalled_NonUnique()
    {
        var metadata = await GetIndexMetadataAsync("PlayerIdentities", "IX_PlayerIdentities_PlayerId");

        metadata.Exists.Should().BeTrue("reverse lookup from player to identities needs an index");
        metadata.IsUnique.Should().BeFalse("one player may have many identities");
    }

    /// <summary>
    /// SQL Server normalizes filtered-index predicates (e.g. lowercases identifiers
    /// inconsistently across versions, may or may not include outer parentheses/spacing).
    /// Strip whitespace, outer parentheses, and case differences so the assertion compares
    /// semantics rather than exact server-version-dependent formatting.
    /// </summary>
    private static string NormalizeFilter(string filterDefinition)
    {
        // Parentheses are pure grouping noise here (SQL Server's normalized form wraps the
        // whole predicate and/or the literal in parens, inconsistently across versions), so
        // strip all of them along with whitespace and compare only the semantic content.
        return new string(filterDefinition
                .Where(c => !char.IsWhiteSpace(c) && c != '(' && c != ')')
                .ToArray())
            .ToLowerInvariant();
    }

    private async Task<(bool Exists, bool IsUnique, string? FilterDefinition)> GetIndexMetadataAsync(string tableName, string indexName)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.is_unique, i.filter_definition
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            WHERE t.name = @tableName AND i.name = @indexName
            """;
        command.Parameters.AddWithValue("@tableName", tableName);
        command.Parameters.AddWithValue("@indexName", indexName);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return (false, false, null);
        }

        var isUnique = reader.GetBoolean(0);
        var filterDefinition = reader.IsDBNull(1) ? null : reader.GetString(1);
        return (true, isUnique, filterDefinition);
    }
}
