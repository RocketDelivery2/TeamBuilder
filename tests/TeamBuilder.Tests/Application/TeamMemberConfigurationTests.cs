using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Verifies the EF Core model metadata for the TeamMember unique membership index.
/// This proves EF's intent/configuration; it does NOT prove SQL Server enforces the
/// constraint at runtime (the InMemory provider does not honor unique indexes or
/// filtered indexes) - that enforcement can only be verified against a real SQL Server.
/// </summary>
public class TeamMemberConfigurationTests
{
    [Fact]
    public void Model_ShouldMarkTeamIdPlayerIdIndex_AsUnique_WithActiveFilter()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var context = new TeamBuilderDbContext(options);

        // Act
        var entityType = context.Model.FindEntityType(typeof(TeamMember));
        var index = entityType!.GetIndexes()
            .FirstOrDefault(i => i.Properties.Select(p => p.Name).SequenceEqual(["TeamId", "PlayerId"]));

        // Assert
        index.Should().NotBeNull();
        index!.IsUnique.Should().BeTrue();
        index.GetDatabaseName().Should().Be("UX_TeamMembers_TeamId_PlayerId");
        index.GetFilter().Should().Be("[IsActive] = 1");
    }
}
