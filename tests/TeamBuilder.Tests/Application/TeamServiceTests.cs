using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Application;

public class TeamServiceTests : IDisposable
{
    private readonly TeamBuilderDbContext _context;
    private readonly TeamService _teamService;

    public TeamServiceTests()
    {
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TeamBuilderDbContext(options);
        _teamService = new TeamService(_context);
    }

    [Fact]
    public async Task CreateAsync_ShouldCreateTeam_Successfully()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var createTeamDto = new CreateTeamDto
        {
            Name = "Test Team",
            Description = "A test team",
            MaxMembers = 10,
            Region = "NA",
            Category = "Gaming",
            Tags = "fps"
        };

        // Act
        var result = await _teamService.CreateAsync(createTeamDto, ownerId);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("Test Team");
        result.OwnerId.Should().Be(ownerId);
        result.Status.Should().Be(TeamStatus.Recruiting);

        var teamInDb = await _context.Teams.FindAsync(result.Id);
        teamInDb.Should().NotBeNull();
        teamInDb!.Name.Should().Be("Test Team");
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnTeam_WhenExists()
    {
        // Arrange
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Existing Team",
            MaxMembers = 10,
            Status = TeamStatus.Active,
            CurrentMemberCount = 0
        };
        _context.Teams.Add(team);
        await _context.SaveChangesAsync();

        // Act
        var result = await _teamService.GetByIdAsync(team.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(team.Id);
        result.Name.Should().Be("Existing Team");
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_WhenNotExists()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var result = await _teamService.GetByIdAsync(nonExistentId);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_ShouldUpdateTeam_Successfully()
    {
        // Arrange
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Original Name",
            MaxMembers = 10,
            Status = TeamStatus.Recruiting,
            CurrentMemberCount = 0
        };
        _context.Teams.Add(team);
        await _context.SaveChangesAsync();

        var updateDto = new UpdateTeamDto
        {
            Name = "Updated Name",
            Status = TeamStatus.Active
        };

        // Act
        var result = await _teamService.UpdateAsync(team.Id, updateDto);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Updated Name");
        result.Status.Should().Be(TeamStatus.Active);

        var updatedTeam = await _context.Teams.FindAsync(team.Id);
        updatedTeam!.Name.Should().Be("Updated Name");
        updatedTeam.Status.Should().Be(TeamStatus.Active);
    }

    [Fact]
    public async Task DeleteAsync_ShouldDeleteTeam_Successfully()
    {
        // Arrange
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Team to Delete",
            MaxMembers = 10,
            Status = TeamStatus.Active,
            CurrentMemberCount = 0
        };
        _context.Teams.Add(team);
        await _context.SaveChangesAsync();

        // Act
        var result = await _teamService.DeleteAsync(team.Id);

        // Assert
        result.Should().BeTrue();
        var deletedTeam = await _context.Teams.FindAsync(team.Id);
        deletedTeam.Should().BeNull();
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnPaginatedResults()
    {
        // Arrange
        for (int i = 0; i < 15; i++)
        {
            _context.Teams.Add(new Team
            {
                Id = Guid.NewGuid(),
                Name = $"Team {i}",
                MaxMembers = 10,
                Status = TeamStatus.Active,
                CurrentMemberCount = 0
            });
        }
        await _context.SaveChangesAsync();

        // Act
        var result = await _teamService.GetAllAsync(page: 1, pageSize: 10);

        // Assert
        result.Should().NotBeNull();
        result.Items.Should().HaveCount(10);
        result.TotalCount.Should().Be(15);
        result.TotalPages.Should().Be(2);
        result.HasNextPage.Should().BeTrue();
        result.HasPreviousPage.Should().BeFalse();
    }

    [Fact]
    public async Task GetAllAsync_ShouldFilterByCategory()
    {
        // Arrange
        _context.Teams.Add(new Team { Id = Guid.NewGuid(), Name = "Gaming Team", Category = "Gaming", MaxMembers = 5 });
        _context.Teams.Add(new Team { Id = Guid.NewGuid(), Name = "Sports Team", Category = "Sports", MaxMembers = 5 });
        _context.Teams.Add(new Team { Id = Guid.NewGuid(), Name = "Gaming Team 2", Category = "Gaming", MaxMembers = 5 });
        await _context.SaveChangesAsync();

        // Act
        var result = await _teamService.GetAllAsync(1, 10, category: "Gaming");

        // Assert
        result.TotalCount.Should().Be(2);
        result.Items.Should().OnlyContain(t => t.Category == "Gaming");
    }

    [Fact]
    public async Task GetAllAsync_ShouldFilterByRegion()
    {
        // Arrange
        _context.Teams.Add(new Team { Id = Guid.NewGuid(), Name = "NA Team", Region = "NA", MaxMembers = 5 });
        _context.Teams.Add(new Team { Id = Guid.NewGuid(), Name = "EU Team", Region = "EU", MaxMembers = 5 });
        await _context.SaveChangesAsync();

        // Act
        var result = await _teamService.GetAllAsync(1, 10, region: "EU");

        // Assert
        result.TotalCount.Should().Be(1);
        result.Items.Single().Region.Should().Be("EU");
    }

    [Fact]
    public async Task GetAllAsync_ShouldFilterByStatus()
    {
        // Arrange
        _context.Teams.Add(new Team { Id = Guid.NewGuid(), Name = "Active Team", Status = TeamStatus.Active, MaxMembers = 5 });
        _context.Teams.Add(new Team { Id = Guid.NewGuid(), Name = "Recruiting Team", Status = TeamStatus.Recruiting, MaxMembers = 5 });
        _context.Teams.Add(new Team { Id = Guid.NewGuid(), Name = "Full Team", Status = TeamStatus.Full, MaxMembers = 5 });
        await _context.SaveChangesAsync();

        // Act
        var result = await _teamService.GetAllAsync(1, 10, status: TeamStatus.Recruiting);

        // Assert
        result.TotalCount.Should().Be(1);
        result.Items.Single().Status.Should().Be(TeamStatus.Recruiting);
    }

    [Fact]
    public async Task UpdateAsync_ShouldReturnNull_WhenNotFound()
    {
        // Arrange
        var updateDto = new UpdateTeamDto { Name = "Ghost Team" };

        // Act
        var result = await _teamService.UpdateAsync(Guid.NewGuid(), updateDto);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ShouldReturnFalse_WhenNotFound()
    {
        // Act
        var result = await _teamService.DeleteAsync(Guid.NewGuid());

        // Assert
        result.Should().BeFalse();
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
