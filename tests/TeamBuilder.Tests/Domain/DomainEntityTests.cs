using FluentAssertions;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Tests.Domain;

public class TeamEventTests
{
    [Fact]
    public void TeamEvent_ShouldInitialize_WithDefaultValues()
    {
        // Arrange & Act
        var teamEvent = new TeamEvent();

        // Assert
        teamEvent.Id.Should().Be(Guid.Empty);
        teamEvent.Name.Should().BeEmpty();
        teamEvent.CurrentParticipantCount.Should().Be(0);
        teamEvent.RosterEntries.Should().BeEmpty();
    }

    [Fact]
    public void TeamEvent_ShouldSetProperties_Correctly()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var eventDate = DateTime.UtcNow.AddDays(7);

        // Act
        var teamEvent = new TeamEvent
        {
            Id = eventId,
            Name = "Championship Event",
            Description = "Annual championship",
            EventDateUtc = eventDate,
            Status = EventStatus.Open,
            Category = "Gaming",
            Tags = "fps,tournament",
            Location = "Online",
            Region = "NA",
            MaxParticipants = 64,
            CurrentParticipantCount = 12,
            TeamId = teamId,
            HostId = hostId
        };

        // Assert
        teamEvent.Id.Should().Be(eventId);
        teamEvent.Name.Should().Be("Championship Event");
        teamEvent.Description.Should().Be("Annual championship");
        teamEvent.EventDateUtc.Should().Be(eventDate);
        teamEvent.Status.Should().Be(EventStatus.Open);
        teamEvent.Category.Should().Be("Gaming");
        teamEvent.Tags.Should().Be("fps,tournament");
        teamEvent.Location.Should().Be("Online");
        teamEvent.Region.Should().Be("NA");
        teamEvent.MaxParticipants.Should().Be(64);
        teamEvent.CurrentParticipantCount.Should().Be(12);
        teamEvent.TeamId.Should().Be(teamId);
        teamEvent.HostId.Should().Be(hostId);
    }

    [Fact]
    public void TeamEvent_ShouldSupportNullOptionalFields()
    {
        // Arrange & Act
        var teamEvent = new TeamEvent
        {
            Id = Guid.NewGuid(),
            Name = "Minimal Event",
            EventDateUtc = DateTime.UtcNow.AddDays(1),
            MaxParticipants = 10
        };

        // Assert
        teamEvent.Description.Should().BeNull();
        teamEvent.Category.Should().BeNull();
        teamEvent.Tags.Should().BeNull();
        teamEvent.Location.Should().BeNull();
        teamEvent.Region.Should().BeNull();
        teamEvent.TeamId.Should().BeNull();
        teamEvent.HostId.Should().BeNull();
        teamEvent.Team.Should().BeNull();
        teamEvent.Host.Should().BeNull();
    }

    [Fact]
    public void TeamEvent_ShouldSupportAllEventStatusValues()
    {
        // Arrange & Act & Assert
        foreach (var status in Enum.GetValues<EventStatus>())
        {
            var teamEvent = new TeamEvent { Status = status };
            teamEvent.Status.Should().Be(status);
        }
    }
}

public class TeamMemberTests
{
    [Fact]
    public void TeamMember_ShouldInitialize_WithDefaultValues()
    {
        // Arrange & Act
        var member = new TeamMember();

        // Assert
        member.Id.Should().Be(Guid.Empty);
        member.TeamId.Should().Be(Guid.Empty);
        member.PlayerId.Should().Be(Guid.Empty);
        member.IsActive.Should().BeFalse();
    }

    [Fact]
    public void TeamMember_ShouldSetProperties_Correctly()
    {
        // Arrange
        var memberId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        var joinDate = DateTime.UtcNow.AddDays(-30);

        // Act
        var member = new TeamMember
        {
            Id = memberId,
            TeamId = teamId,
            PlayerId = playerId,
            Role = TeamRole.Leader,
            JoinedAtUtc = joinDate,
            IsActive = true
        };

        // Assert
        member.Id.Should().Be(memberId);
        member.TeamId.Should().Be(teamId);
        member.PlayerId.Should().Be(playerId);
        member.Role.Should().Be(TeamRole.Leader);
        member.JoinedAtUtc.Should().Be(joinDate);
        member.IsActive.Should().BeTrue();
    }

    [Fact]
    public void TeamMember_ShouldSupportAllTeamRoles()
    {
        // Arrange & Act & Assert
        foreach (var role in Enum.GetValues<TeamRole>())
        {
            var member = new TeamMember { Role = role };
            member.Role.Should().Be(role);
        }
    }

    [Fact]
    public void TeamMember_IsActive_ShouldToggle_Correctly()
    {
        // Arrange
        var member = new TeamMember { IsActive = true };

        // Act
        member.IsActive = false;

        // Assert
        member.IsActive.Should().BeFalse();
    }
}

public class JoinRequestTests
{
    [Fact]
    public void JoinRequest_ShouldInitialize_WithDefaultValues()
    {
        // Arrange & Act
        var request = new JoinRequest();

        // Assert
        request.Id.Should().Be(Guid.Empty);
        request.TeamId.Should().Be(Guid.Empty);
        request.PlayerId.Should().Be(Guid.Empty);
        request.Message.Should().BeNull();
        request.ProcessedAtUtc.Should().BeNull();
        request.ProcessedByUserId.Should().BeNull();
    }

    [Fact]
    public void JoinRequest_ShouldSetProperties_Correctly()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        var processedBy = Guid.NewGuid();
        var requestedAt = DateTime.UtcNow.AddHours(-2);
        var processedAt = DateTime.UtcNow;

        // Act
        var request = new JoinRequest
        {
            Id = requestId,
            TeamId = teamId,
            PlayerId = playerId,
            Status = RequestStatus.Approved,
            Message = "Please let me join!",
            RequestedAtUtc = requestedAt,
            ProcessedAtUtc = processedAt,
            ProcessedByUserId = processedBy
        };

        // Assert
        request.Id.Should().Be(requestId);
        request.TeamId.Should().Be(teamId);
        request.PlayerId.Should().Be(playerId);
        request.Status.Should().Be(RequestStatus.Approved);
        request.Message.Should().Be("Please let me join!");
        request.RequestedAtUtc.Should().Be(requestedAt);
        request.ProcessedAtUtc.Should().Be(processedAt);
        request.ProcessedByUserId.Should().Be(processedBy);
    }

    [Fact]
    public void JoinRequest_ShouldSupportAllRequestStatusValues()
    {
        // Arrange & Act & Assert
        foreach (var status in Enum.GetValues<RequestStatus>())
        {
            var request = new JoinRequest { Status = status };
            request.Status.Should().Be(status);
        }
    }

    [Fact]
    public void JoinRequest_PendingRequest_ShouldNotBeProcessed()
    {
        // Arrange & Act
        var request = new JoinRequest
        {
            Status = RequestStatus.Pending,
            RequestedAtUtc = DateTime.UtcNow
        };

        // Assert
        request.Status.Should().Be(RequestStatus.Pending);
        request.ProcessedAtUtc.Should().BeNull();
        request.ProcessedByUserId.Should().BeNull();
    }
}

public class RosterEntryTests
{
    [Fact]
    public void RosterEntry_ShouldInitialize_WithDefaultValues()
    {
        // Arrange & Act
        var entry = new RosterEntry();

        // Assert
        entry.Id.Should().Be(Guid.Empty);
        entry.EventId.Should().Be(Guid.Empty);
        entry.PlayerId.Should().Be(Guid.Empty);
        entry.IsConfirmed.Should().BeFalse();
        entry.Position.Should().BeNull();
        entry.Notes.Should().BeNull();
    }

    [Fact]
    public void RosterEntry_ShouldSetProperties_Correctly()
    {
        // Arrange
        var entryId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        var registeredAt = DateTime.UtcNow.AddHours(-1);

        // Act
        var entry = new RosterEntry
        {
            Id = entryId,
            EventId = eventId,
            PlayerId = playerId,
            Position = "Tank",
            Notes = "Primary tank role",
            IsConfirmed = true,
            RegisteredAtUtc = registeredAt
        };

        // Assert
        entry.Id.Should().Be(entryId);
        entry.EventId.Should().Be(eventId);
        entry.PlayerId.Should().Be(playerId);
        entry.Position.Should().Be("Tank");
        entry.Notes.Should().Be("Primary tank role");
        entry.IsConfirmed.Should().BeTrue();
        entry.RegisteredAtUtc.Should().Be(registeredAt);
    }

    [Fact]
    public void RosterEntry_IsConfirmed_ShouldToggle_Correctly()
    {
        // Arrange
        var entry = new RosterEntry { IsConfirmed = false };

        // Act
        entry.IsConfirmed = true;

        // Assert
        entry.IsConfirmed.Should().BeTrue();
    }
}

public class RosterImportTests
{
    [Fact]
    public void RosterImport_ShouldInitialize_WithDefaultValues()
    {
        // Arrange & Act
        var import = new RosterImport();

        // Assert
        import.Id.Should().Be(Guid.Empty);
        import.SourceName.Should().BeEmpty();
        import.SourceType.Should().BeEmpty();
        import.RawData.Should().BeEmpty();
        import.IsProcessed.Should().BeFalse();
        import.ProcessedAtUtc.Should().BeNull();
        import.ProcessingNotes.Should().BeNull();
        import.ImportedByUserId.Should().BeNull();
    }

    [Fact]
    public void RosterImport_ShouldSetProperties_Correctly()
    {
        // Arrange
        var importId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var processedAt = DateTime.UtcNow;

        // Act
        var import = new RosterImport
        {
            Id = importId,
            SourceName = "TeamSpreadsheet",
            SourceType = "CSV",
            RawData = "Name,Role\nPlayer1,Tank",
            IsProcessed = true,
            ProcessedAtUtc = processedAt,
            ProcessingNotes = "Processed 1 player",
            ImportedByUserId = userId
        };

        // Assert
        import.Id.Should().Be(importId);
        import.SourceName.Should().Be("TeamSpreadsheet");
        import.SourceType.Should().Be("CSV");
        import.RawData.Should().Be("Name,Role\nPlayer1,Tank");
        import.IsProcessed.Should().BeTrue();
        import.ProcessedAtUtc.Should().Be(processedAt);
        import.ProcessingNotes.Should().Be("Processed 1 player");
        import.ImportedByUserId.Should().Be(userId);
    }
}

public class BaseEntityTests
{
    private class ConcreteEntity : BaseEntity { }

    [Fact]
    public void BaseEntity_ShouldInitialize_RowVersion_AsEmptyArray()
    {
        // Arrange & Act
        var entity = new ConcreteEntity();

        // Assert
        entity.RowVersion.Should().NotBeNull();
        entity.RowVersion.Should().BeEmpty();
    }

    [Fact]
    public void BaseEntity_ShouldHaveNullableUpdatedAt()
    {
        // Arrange & Act
        var entity = new ConcreteEntity();

        // Assert
        entity.UpdatedAtUtc.Should().BeNull();
    }

    [Fact]
    public void BaseEntity_ShouldSetTimestamps_Correctly()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var updated = now.AddMinutes(5);

        // Act
        var entity = new ConcreteEntity
        {
            CreatedAtUtc = now,
            UpdatedAtUtc = updated
        };

        // Assert
        entity.CreatedAtUtc.Should().Be(now);
        entity.UpdatedAtUtc.Should().Be(updated);
    }
}
