using FluentAssertions;
using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Tests.Domain;

public class PlayerTests
{
    [Fact]
    public void Player_ShouldInitialize_WithDefaultValues()
    {
        // Arrange & Act
        var player = new Player();

        // Assert
        player.Id.Should().Be(Guid.Empty);
        player.Username.Should().BeEmpty();
        player.TeamMemberships.Should().BeEmpty();
        player.OwnedTeams.Should().BeEmpty();
        player.HostedEvents.Should().BeEmpty();
        player.RosterEntries.Should().BeEmpty();
        player.JoinRequests.Should().BeEmpty();
    }

    [Fact]
    public void Player_ShouldSetProperties_Correctly()
    {
        // Arrange
        var playerId = Guid.NewGuid();
        var username = "TestPlayer";
        var email = "test@example.com";

        // Act
        var player = new Player
        {
            Id = playerId,
            Username = username,
            Email = email,
            DisplayName = "Test Display Name",
            Bio = "A test bio",
            Region = "EU",
            AvatarUrl = "https://example.com/avatar.png"
        };

        // Assert
        player.Id.Should().Be(playerId);
        player.Username.Should().Be(username);
        player.Email.Should().Be(email);
        player.DisplayName.Should().Be("Test Display Name");
        player.Bio.Should().Be("A test bio");
        player.Region.Should().Be("EU");
        player.AvatarUrl.Should().Be("https://example.com/avatar.png");
    }
}
