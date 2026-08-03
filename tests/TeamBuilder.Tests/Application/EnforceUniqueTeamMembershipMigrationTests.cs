using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using TeamBuilder.Infrastructure.Persistence.Migrations;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Verifies the shape of the EnforceUniqueTeamMembership migration: a duplicate-data
/// preflight check runs before the unique index is created, and Down only reverses what
/// Up added (drop the new unique index, restore the prior non-unique index).
/// </summary>
public class EnforceUniqueTeamMembershipMigrationTests
{
    [Fact]
    public void Up_ShouldRunDuplicatePreflightCheck_BeforeCreatingTheUniqueIndex()
    {
        // Arrange
        var migration = new EnforceUniqueTeamMembership();
        var migrationBuilder = new MigrationBuilder(activeProvider: "Microsoft.EntityFrameworkCore.SqlServer");

        // Act
        InvokeMigrationMethod(migration, "Up", migrationBuilder);

        // Assert
        var operations = migrationBuilder.Operations;

        var sqlOperationIndex = operations.FindIndex(op =>
            op is SqlOperation sqlOp && sqlOp.Sql.Contains("HAVING COUNT(*) > 1", StringComparison.Ordinal));
        var createIndexOperationIndex = operations.FindIndex(op =>
            op is CreateIndexOperation createIndexOp &&
            createIndexOp.Name == "UX_TeamMembers_TeamId_PlayerId");

        sqlOperationIndex.Should().BeGreaterThanOrEqualTo(0, "the migration must contain a duplicate-data preflight check");
        createIndexOperationIndex.Should().BeGreaterThanOrEqualTo(0, "the migration must create the unique index");
        sqlOperationIndex.Should().BeLessThan(createIndexOperationIndex,
            "the preflight check must run before the unique index is created");

        var sqlOperation = (SqlOperation)operations[sqlOperationIndex];
        sqlOperation.Sql.Should().Contain("RAISERROR", "duplicates must fail the migration with a clear message");
        sqlOperation.Sql.Should().Contain("IsActive", "the preflight must match the filtered unique index's semantics");

        var createIndexOperation = (CreateIndexOperation)operations[createIndexOperationIndex];
        createIndexOperation.IsUnique.Should().BeTrue();
        createIndexOperation.Columns.Should().BeEquivalentTo(["TeamId", "PlayerId"]);
        createIndexOperation.Filter.Should().Be("[IsActive] = 1");
    }

    [Fact]
    public void Down_ShouldOnlyRemoveTheNewIndex_AndRestoreThePriorIndex()
    {
        // Arrange
        var migration = new EnforceUniqueTeamMembership();
        var migrationBuilder = new MigrationBuilder(activeProvider: "Microsoft.EntityFrameworkCore.SqlServer");

        // Act
        InvokeMigrationMethod(migration, "Down", migrationBuilder);

        // Assert
        var operations = migrationBuilder.Operations;

        operations.Should().HaveCount(2, "Down must only undo what Up added: drop the new unique index and restore the prior one");

        var dropOperation = operations.OfType<DropIndexOperation>().Single();
        dropOperation.Name.Should().Be("UX_TeamMembers_TeamId_PlayerId");
        dropOperation.Table.Should().Be("TeamMembers");

        var recreateOperation = operations.OfType<CreateIndexOperation>().Single();
        recreateOperation.Name.Should().Be("IX_TeamMembers_TeamId_PlayerId");
        recreateOperation.IsUnique.Should().BeFalse();
        recreateOperation.Filter.Should().BeNull();
    }

    private static void InvokeMigrationMethod(Migration migration, string methodName, MigrationBuilder migrationBuilder)
    {
        var method = typeof(EnforceUniqueTeamMembership).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: [typeof(MigrationBuilder)],
            modifiers: null)
            ?? throw new InvalidOperationException($"Unable to locate the {methodName} method via reflection.");

        method.Invoke(migration, [migrationBuilder]);
    }
}
