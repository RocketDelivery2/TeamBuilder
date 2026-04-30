using FluentAssertions;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Tests.Domain;

public class TeamTests
{
    [Fact]
    public void Team_ShouldInitialize_WithDefaultValues()
    {
        // Arrange & Act
        var team = new Team();

        // Assert
        team.Id.Should().Be(Guid.Empty);
        team.Name.Should().BeEmpty();
        team.Members.Should().BeEmpty();
        team.Events.Should().BeEmpty();
        team.JoinRequests.Should().BeEmpty();
    }

    [Fact]
    public void Team_ShouldSetProperties_Correctly()
    {
        // Arrange
        var teamId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var teamName = "Test Team";
        var description = "A test team description";

        // Act
        var team = new Team
        {
            Id = teamId,
            Name = teamName,
            Description = description,
            Status = TeamStatus.Recruiting,
            MaxMembers = 10,
            CurrentMemberCount = 3,
            Region = "NA",
            Category = "Gaming",
            Tags = "fps,competitive",
            OwnerId = ownerId
        };

        // Assert
        team.Id.Should().Be(teamId);
        team.Name.Should().Be(teamName);
        team.Description.Should().Be(description);
        team.Status.Should().Be(TeamStatus.Recruiting);
        team.MaxMembers.Should().Be(10);
        team.CurrentMemberCount.Should().Be(3);
        team.Region.Should().Be("NA");
        team.Category.Should().Be("Gaming");
        team.Tags.Should().Be("fps,competitive");
        team.OwnerId.Should().Be(ownerId);
    }
}
