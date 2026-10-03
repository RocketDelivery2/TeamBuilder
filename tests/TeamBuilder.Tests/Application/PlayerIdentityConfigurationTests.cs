using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Data.Configurations;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Verifies the EF Core model metadata for PlayerIdentity. This proves EF's configuration only;
/// SQL Server enforcement of the unique index is covered by
/// <see cref="PlayerIdentityUniqueIndexIntegrationTests"/>.
/// </summary>
public class PlayerIdentityConfigurationTests
{
    // The SQL Server provider is used (never connected) so relational metadata such as
    // column collation is present in the design-time model.
    private static IModel CreateDesignTimeModel()
    {
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True;")
            .Options;

        using var context = new TeamBuilderDbContext(options);
        return context.GetService<IDesignTimeModel>().Model;
    }

    private static IEntityType PlayerIdentityEntity() =>
        CreateDesignTimeModel().FindEntityType(typeof(PlayerIdentity))!;

    [Fact]
    public void Model_ShouldExposePlayerIdentity_MappedToPlayerIdentitiesTable()
    {
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        using var context = new TeamBuilderDbContext(options);

        context.PlayerIdentities.Should().NotBeNull();
        context.Model.FindEntityType(typeof(PlayerIdentity)).Should().NotBeNull();
        PlayerIdentityEntity().GetTableName().Should().Be("PlayerIdentities");
    }

    [Theory]
    [InlineData(nameof(PlayerIdentity.Issuer), false, 512)]
    [InlineData(nameof(PlayerIdentity.Subject), false, 255)]
    [InlineData(nameof(PlayerIdentity.Provider), false, 100)]
    [InlineData(nameof(PlayerIdentity.TenantId), true, 100)]
    public void Model_ShouldConfigureStringColumns(string propertyName, bool isNullable, int maxLength)
    {
        var property = PlayerIdentityEntity().FindProperty(propertyName)!;

        property.IsNullable.Should().Be(isNullable);
        property.GetMaxLength().Should().Be(maxLength);
    }

    [Theory]
    [InlineData(nameof(PlayerIdentity.Issuer))]
    [InlineData(nameof(PlayerIdentity.Subject))]
    public void Model_ShouldUseOrdinalCollation_ForIdentityKeyColumns(string propertyName)
    {
        PlayerIdentityEntity().FindProperty(propertyName)!.GetCollation().Should().Be("Latin1_General_100_BIN2");
    }

    [Fact]
    public void Model_ShouldRequirePlayer_WithCascadeDelete_AndPlayerIdentitiesNavigation()
    {
        var foreignKey = PlayerIdentityEntity().GetForeignKeys().Single();

        foreignKey.PrincipalEntityType.ClrType.Should().Be(typeof(Player));
        foreignKey.Properties.Select(p => p.Name).Should().Equal(nameof(PlayerIdentity.PlayerId));
        foreignKey.IsRequired.Should().BeTrue();
        foreignKey.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
        foreignKey.PrincipalToDependent!.Name.Should().Be(nameof(Player.Identities));
    }

    [Fact]
    public void Model_ShouldDefineUniqueIssuerSubjectIndex_WithoutFilter()
    {
        var index = PlayerIdentityEntity().GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(PlayerIdentity.Issuer), nameof(PlayerIdentity.Subject)]));

        index.IsUnique.Should().BeTrue();
        index.GetDatabaseName().Should().Be(PlayerIdentityConfiguration.IssuerSubjectIndexName);
        index.GetFilter().Should().BeNull();
    }

    [Fact]
    public void Model_ShouldIndexPlayerId_NonUnique_ForReverseLookup()
    {
        var index = PlayerIdentityEntity().GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(PlayerIdentity.PlayerId)]));

        index.IsUnique.Should().BeFalse();
    }

    [Fact]
    public void Model_ShouldNotIncludeProviderOrTenantId_InAnyUniqueIndex()
    {
        PlayerIdentityEntity().GetIndexes()
            .Where(i => i.IsUnique)
            .SelectMany(i => i.Properties.Select(p => p.Name))
            .Should().NotContain([nameof(PlayerIdentity.Provider), nameof(PlayerIdentity.TenantId)]);
    }
}
