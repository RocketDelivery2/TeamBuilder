using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Application;

public class EventServiceTests : IDisposable
{
    private readonly TeamBuilderDbContext _context;
    private readonly EventService _eventService;

    public EventServiceTests()
    {
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TeamBuilderDbContext(options);
        _eventService = new EventService(_context);
    }

    [Fact]
    public async Task CreateAsync_ShouldCreateEvent_Successfully()
    {
        // Arrange
        var hostId = Guid.NewGuid();
        var createDto = new CreateEventDto
        {
            Name = "Test Event",
            Description = "A test event",
            EventDateUtc = DateTime.UtcNow.AddDays(7),
            Category = "Gaming",
            Region = "NA",
            MaxParticipants = 20
        };

        // Act
        var result = await _eventService.CreateAsync(createDto, hostId);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("Test Event");
        result.Description.Should().Be("A test event");
        result.Status.Should().Be(EventStatus.Planned);
        result.HostId.Should().Be(hostId);
        result.CurrentParticipantCount.Should().Be(0);
        result.MaxParticipants.Should().Be(20);

        var eventInDb = await _context.Events.FindAsync(result.Id);
        eventInDb.Should().NotBeNull();
        eventInDb!.Name.Should().Be("Test Event");
    }

    [Fact]
    public async Task CreateAsync_ShouldLinkToTeam_WhenTeamIdProvided()
    {
        // Arrange
        var team = new Team { Id = Guid.NewGuid(), Name = "Test Team", MaxMembers = 10, CurrentMemberCount = 0 };
        _context.Teams.Add(team);
        await _context.SaveChangesAsync();

        var createDto = new CreateEventDto
        {
            Name = "Team Event",
            EventDateUtc = DateTime.UtcNow.AddDays(5),
            TeamId = team.Id
        };

        // Act
        var result = await _eventService.CreateAsync(createDto, Guid.NewGuid());

        // Assert
        result.TeamId.Should().Be(team.Id);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnEvent_WhenExists()
    {
        // Arrange
        var teamEvent = new TeamEvent
        {
            Id = Guid.NewGuid(),
            Name = "Existing Event",
            EventDateUtc = DateTime.UtcNow.AddDays(3),
            Status = EventStatus.Open,
            MaxParticipants = 10,
            CurrentParticipantCount = 0
        };
        _context.Events.Add(teamEvent);
        await _context.SaveChangesAsync();

        // Act
        var result = await _eventService.GetByIdAsync(teamEvent.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(teamEvent.Id);
        result.Name.Should().Be("Existing Event");
        result.Status.Should().Be(EventStatus.Open);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_WhenNotExists()
    {
        // Act
        var result = await _eventService.GetByIdAsync(Guid.NewGuid());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByIdAsync_ShouldIncludeTeamAndHost_WhenPresent()
    {
        // Arrange
        var team = new Team { Id = Guid.NewGuid(), Name = "Event Team", MaxMembers = 5, CurrentMemberCount = 0 };
        var host = new Player { Id = Guid.NewGuid(), Username = "HostPlayer" };
        _context.Teams.Add(team);
        _context.Players.Add(host);

        var teamEvent = new TeamEvent
        {
            Id = Guid.NewGuid(),
            Name = "Full Event",
            EventDateUtc = DateTime.UtcNow.AddDays(1),
            Status = EventStatus.Planned,
            MaxParticipants = 10,
            CurrentParticipantCount = 0,
            TeamId = team.Id,
            HostId = host.Id
        };
        _context.Events.Add(teamEvent);
        await _context.SaveChangesAsync();

        // Act
        var result = await _eventService.GetByIdAsync(teamEvent.Id);

        // Assert
        result.Should().NotBeNull();
        result!.TeamName.Should().Be("Event Team");
        result.HostUsername.Should().Be("HostPlayer");
        result.TeamId.Should().Be(team.Id);
        result.HostId.Should().Be(host.Id);
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnPaginatedResults()
    {
        // Arrange
        var baseDate = DateTime.UtcNow;
        for (int i = 0; i < 15; i++)
        {
            _context.Events.Add(new TeamEvent
            {
                Id = Guid.NewGuid(),
                Name = $"Event {i}",
                EventDateUtc = baseDate.AddDays(i),
                Status = EventStatus.Planned,
                MaxParticipants = 10,
                CurrentParticipantCount = 0
            });
        }
        await _context.SaveChangesAsync();

        // Act
        var result = await _eventService.GetAllAsync(page: 1, pageSize: 10);

        // Assert
        result.Should().NotBeNull();
        result.Items.Should().HaveCount(10);
        result.TotalCount.Should().Be(15);
        result.TotalPages.Should().Be(2);
        result.HasNextPage.Should().BeTrue();
        result.HasPreviousPage.Should().BeFalse();
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnSecondPage_Correctly()
    {
        // Arrange
        var baseDate = DateTime.UtcNow;
        for (int i = 0; i < 15; i++)
        {
            _context.Events.Add(new TeamEvent
            {
                Id = Guid.NewGuid(),
                Name = $"Event {i}",
                EventDateUtc = baseDate.AddDays(i),
                Status = EventStatus.Planned,
                MaxParticipants = 10,
                CurrentParticipantCount = 0
            });
        }
        await _context.SaveChangesAsync();

        // Act
        var result = await _eventService.GetAllAsync(page: 2, pageSize: 10);

        // Assert
        result.Items.Should().HaveCount(5);
        result.Page.Should().Be(2);
        result.HasPreviousPage.Should().BeTrue();
        result.HasNextPage.Should().BeFalse();
    }

    [Fact]
    public async Task GetAllAsync_ShouldFilterByCategory()
    {
        // Arrange
        _context.Events.AddRange(
            new TeamEvent { Id = Guid.NewGuid(), Name = "Gaming Event", EventDateUtc = DateTime.UtcNow.AddDays(1), Status = EventStatus.Planned, MaxParticipants = 10, Category = "Gaming" },
            new TeamEvent { Id = Guid.NewGuid(), Name = "Sports Event", EventDateUtc = DateTime.UtcNow.AddDays(2), Status = EventStatus.Planned, MaxParticipants = 10, Category = "Sports" },
            new TeamEvent { Id = Guid.NewGuid(), Name = "Gaming Event 2", EventDateUtc = DateTime.UtcNow.AddDays(3), Status = EventStatus.Planned, MaxParticipants = 10, Category = "Gaming" }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _eventService.GetAllAsync(1, 20, category: "Gaming");

        // Assert
        result.Items.Should().HaveCount(2);
        result.Items.Should().OnlyContain(e => e.Category == "Gaming");
        result.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAllAsync_ShouldFilterByRegion()
    {
        // Arrange
        _context.Events.AddRange(
            new TeamEvent { Id = Guid.NewGuid(), Name = "NA Event", EventDateUtc = DateTime.UtcNow.AddDays(1), Status = EventStatus.Planned, MaxParticipants = 10, Region = "NA" },
            new TeamEvent { Id = Guid.NewGuid(), Name = "EU Event", EventDateUtc = DateTime.UtcNow.AddDays(2), Status = EventStatus.Planned, MaxParticipants = 10, Region = "EU" },
            new TeamEvent { Id = Guid.NewGuid(), Name = "NA Event 2", EventDateUtc = DateTime.UtcNow.AddDays(3), Status = EventStatus.Planned, MaxParticipants = 10, Region = "NA" }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _eventService.GetAllAsync(1, 20, region: "NA");

        // Assert
        result.Items.Should().HaveCount(2);
        result.Items.Should().OnlyContain(e => e.Region == "NA");
    }

    [Fact]
    public async Task GetAllAsync_ShouldFilterByStatus()
    {
        // Arrange
        _context.Events.AddRange(
            new TeamEvent { Id = Guid.NewGuid(), Name = "Open Event", EventDateUtc = DateTime.UtcNow.AddDays(1), Status = EventStatus.Open, MaxParticipants = 10 },
            new TeamEvent { Id = Guid.NewGuid(), Name = "Planned Event", EventDateUtc = DateTime.UtcNow.AddDays(2), Status = EventStatus.Planned, MaxParticipants = 10 },
            new TeamEvent { Id = Guid.NewGuid(), Name = "Cancelled Event", EventDateUtc = DateTime.UtcNow.AddDays(3), Status = EventStatus.Cancelled, MaxParticipants = 10 }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _eventService.GetAllAsync(1, 20, status: EventStatus.Open);

        // Assert
        result.Items.Should().HaveCount(1);
        result.Items.Should().OnlyContain(e => e.Status == EventStatus.Open);
    }

    [Fact]
    public async Task GetAllAsync_ShouldOrderByEventDate_Ascending()
    {
        // Arrange
        var baseDate = DateTime.UtcNow;
        _context.Events.AddRange(
            new TeamEvent { Id = Guid.NewGuid(), Name = "Third", EventDateUtc = baseDate.AddDays(3), Status = EventStatus.Planned, MaxParticipants = 10 },
            new TeamEvent { Id = Guid.NewGuid(), Name = "First", EventDateUtc = baseDate.AddDays(1), Status = EventStatus.Planned, MaxParticipants = 10 },
            new TeamEvent { Id = Guid.NewGuid(), Name = "Second", EventDateUtc = baseDate.AddDays(2), Status = EventStatus.Planned, MaxParticipants = 10 }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _eventService.GetAllAsync(1, 20);

        // Assert
        result.Items.Select(e => e.Name).Should().ContainInOrder("First", "Second", "Third");
    }

    [Fact]
    public async Task GetAllAsync_ShouldCombineFilters_CategoryAndRegion()
    {
        // Arrange
        _context.Events.AddRange(
            new TeamEvent { Id = Guid.NewGuid(), Name = "Gaming NA", EventDateUtc = DateTime.UtcNow.AddDays(1), Status = EventStatus.Planned, MaxParticipants = 10, Category = "Gaming", Region = "NA" },
            new TeamEvent { Id = Guid.NewGuid(), Name = "Gaming EU", EventDateUtc = DateTime.UtcNow.AddDays(2), Status = EventStatus.Planned, MaxParticipants = 10, Category = "Gaming", Region = "EU" },
            new TeamEvent { Id = Guid.NewGuid(), Name = "Sports NA", EventDateUtc = DateTime.UtcNow.AddDays(3), Status = EventStatus.Planned, MaxParticipants = 10, Category = "Sports", Region = "NA" }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _eventService.GetAllAsync(1, 20, category: "Gaming", region: "NA");

        // Assert
        result.Items.Should().HaveCount(1);
        result.Items.First().Name.Should().Be("Gaming NA");
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnEmpty_WhenNoEventsMatch()
    {
        // Arrange
        _context.Events.Add(new TeamEvent
        {
            Id = Guid.NewGuid(),
            Name = "Only Event",
            EventDateUtc = DateTime.UtcNow.AddDays(1),
            Status = EventStatus.Planned,
            MaxParticipants = 10,
            Region = "NA"
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _eventService.GetAllAsync(1, 20, region: "SEA");

        // Assert
        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task UpdateAsync_ShouldUpdateEvent_Successfully()
    {
        // Arrange
        var teamEvent = new TeamEvent
        {
            Id = Guid.NewGuid(),
            Name = "Original Name",
            EventDateUtc = DateTime.UtcNow.AddDays(5),
            Status = EventStatus.Planned,
            MaxParticipants = 10,
            CurrentParticipantCount = 0
        };
        _context.Events.Add(teamEvent);
        await _context.SaveChangesAsync();

        var updateDto = new UpdateEventDto
        {
            Name = "Updated Name",
            Status = EventStatus.Open,
            MaxParticipants = 25,
            Location = "Online",
            Region = "EU"
        };

        // Act
        var result = await _eventService.UpdateAsync(teamEvent.Id, updateDto);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Updated Name");
        result.Status.Should().Be(EventStatus.Open);
        result.MaxParticipants.Should().Be(25);
        result.Location.Should().Be("Online");
        result.Region.Should().Be("EU");

        var updatedEvent = await _context.Events.FindAsync(teamEvent.Id);
        updatedEvent!.Name.Should().Be("Updated Name");
        updatedEvent.Status.Should().Be(EventStatus.Open);
    }

    [Fact]
    public async Task UpdateAsync_ShouldReturnNull_WhenNotExists()
    {
        // Arrange
        var updateDto = new UpdateEventDto { Name = "New Name" };

        // Act
        var result = await _eventService.UpdateAsync(Guid.NewGuid(), updateDto);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_ShouldOnlyUpdateProvidedFields()
    {
        // Arrange
        var teamEvent = new TeamEvent
        {
            Id = Guid.NewGuid(),
            Name = "Original Name",
            Description = "Original Description",
            EventDateUtc = DateTime.UtcNow.AddDays(5),
            Status = EventStatus.Planned,
            Category = "Gaming",
            MaxParticipants = 10,
            CurrentParticipantCount = 0
        };
        _context.Events.Add(teamEvent);
        await _context.SaveChangesAsync();

        var updateDto = new UpdateEventDto
        {
            Name = "Updated Name"
            // All other fields left null - should not change
        };

        // Act
        var result = await _eventService.UpdateAsync(teamEvent.Id, updateDto);

        // Assert
        result!.Name.Should().Be("Updated Name");
        result.Description.Should().Be("Original Description");
        result.Status.Should().Be(EventStatus.Planned);
        result.Category.Should().Be("Gaming");
    }

    [Fact]
    public async Task UpdateAsync_ShouldCancelEvent_Successfully()
    {
        // Arrange
        var teamEvent = new TeamEvent
        {
            Id = Guid.NewGuid(),
            Name = "Active Event",
            EventDateUtc = DateTime.UtcNow.AddDays(5),
            Status = EventStatus.Open,
            MaxParticipants = 10,
            CurrentParticipantCount = 3
        };
        _context.Events.Add(teamEvent);
        await _context.SaveChangesAsync();

        var updateDto = new UpdateEventDto { Status = EventStatus.Cancelled };

        // Act
        var result = await _eventService.UpdateAsync(teamEvent.Id, updateDto);

        // Assert
        result!.Status.Should().Be(EventStatus.Cancelled);
    }

    [Fact]
    public async Task DeleteAsync_ShouldDeleteEvent_Successfully()
    {
        // Arrange
        var teamEvent = new TeamEvent
        {
            Id = Guid.NewGuid(),
            Name = "Event to Delete",
            EventDateUtc = DateTime.UtcNow.AddDays(2),
            Status = EventStatus.Planned,
            MaxParticipants = 10,
            CurrentParticipantCount = 0
        };
        _context.Events.Add(teamEvent);
        await _context.SaveChangesAsync();

        // Act
        var result = await _eventService.DeleteAsync(teamEvent.Id);

        // Assert
        result.Should().BeTrue();
        var deletedEvent = await _context.Events.FindAsync(teamEvent.Id);
        deletedEvent.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ShouldReturnFalse_WhenNotExists()
    {
        // Act
        var result = await _eventService.DeleteAsync(Guid.NewGuid());

        // Assert
        result.Should().BeFalse();
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
