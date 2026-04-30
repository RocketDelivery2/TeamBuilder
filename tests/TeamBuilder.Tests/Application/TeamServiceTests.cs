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
        result.Description.Should().Be("A test team");
        result.OwnerId.Should().Be(ownerId);
        result.Status.Should().Be(TeamStatus.Recruiting);
        result.CurrentMemberCount.Should().Be(0);
        result.MaxMembers.Should().Be(10);
        result.Region.Should().Be("NA");
        result.Category.Should().Be("Gaming");
        result.Tags.Should().Be("fps");

        var teamInDb = await _context.Teams.FindAsync(result.Id);
        teamInDb.Should().NotBeNull();
        teamInDb!.Name.Should().Be("Test Team");
    }

    [Fact]
    public async Task CreateAsync_ShouldDefaultStatus_ToRecruiting()
    {
        // Arrange
        var createDto = new CreateTeamDto
        {
            Name = "New Team",
            MaxMembers = 5
        };

        // Act
        var result = await _teamService.CreateAsync(createDto, Guid.NewGuid());

        // Assert
        result.Status.Should().Be(TeamStatus.Recruiting);
        result.CurrentMemberCount.Should().Be(0);
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
        result.Status.Should().Be(TeamStatus.Active);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldIncludeOwnerUsername_WhenOwnerExists()
    {
        // Arrange
        var owner = new Player
        {
            Id = Guid.NewGuid(),
            Username = "TeamOwner"
        };

        _context.Players.Add(owner);

        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Owned Team",
            MaxMembers = 5,
            Status = TeamStatus.Recruiting,
            CurrentMemberCount = 0,
            OwnerId = owner.Id,
            Owner = owner
        };

        _context.Teams.Add(team);
        await _context.SaveChangesAsync();

        // Act
        var result = await _teamService.GetByIdAsync(team.Id);

        // Assert
        result.Should().NotBeNull();
        result!.OwnerUsername.Should().Be("TeamOwner");
        result.OwnerId.Should().Be(owner.Id);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_WhenNotExists()
    {
        // Act
        var result = await _teamService.GetByIdAsync(Guid.NewGuid());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnPaginatedResults()
    {
        // Arrange
        for (var i = 0; i < 15; i++)
        {
            _context.Teams.Add(new Team
            {
                Id = Guid.NewGuid(),
                Name = $"Team {i:D2}",
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
    public async Task GetAllAsync_ShouldReturnSecondPage_Correctly()
    {
        // Arrange
        for (var i = 0; i < 15; i++)
        {
            _context.Teams.Add(new Team
            {
                Id = Guid.NewGuid(),
                Name = $"Team {i:D2}",
                MaxMembers = 10,
                Status = TeamStatus.Active,
                CurrentMemberCount = 0
            });
        }

        await _context.SaveChangesAsync();

        // Act
        var result = await _teamService.GetAllAsync(page: 2, pageSize: 10);

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
        _context.Teams.AddRange(
            new Team
            {
                Id = Guid.NewGuid(),
                Name = "Gaming Team",
                MaxMembers = 5,
                CurrentMemberCount = 0,
                Status = TeamStatus.Recruiting,
                Category = "Gaming"
            },
            new Team
            {
                Id = Guid.NewGuid(),
                Name = "Sports Team",
                MaxMembers = 5,
                CurrentMemberCount = 0,
                Status = TeamStatus.Recruiting,
                Category = "Sports"
            },
            new Team
            {
                Id = Guid.NewGuid(),
                Name = "Gaming Team 2",
                MaxMembers = 5,
                CurrentMemberCount = 0,
                Status = TeamStatus.Recruiting,
                Category = "Gaming"
            }
        );

        await _context.SaveChangesAsync();

        // Act
        var result = await _teamService.GetAllAsync(1, 20, category: "Gaming");

        // Assert
        result.Items.Should().HaveCount(2);
        result.TotalCount.Should().Be(2);
        result.Items.Should().OnlyContain(t => t.Category == "Gaming");
    }

    [Fact]
    public async Task GetAllAsync_ShouldFilterByRegion()
    {
        // Arrange
        _context.Teams.AddRange(
            new Team
            {
                Id = Guid.NewGuid(),
                Name = "NA Team",
                MaxMembers = 5,
                CurrentMemberCount = 0,
                Status = TeamStatus.Recruiting,
                Region = "NA"
            },
            new Team
            {
                Id = Guid.NewGuid(),
                Name = "EU Team",
                MaxMembers = 5,
                CurrentMemberCount = 0,
                Status = TeamStatus.Recruiting,
                Region = "EU"
            }
        );

        await _context.SaveChangesAsync();

        // Act
        var result = await _teamService.GetAllAsync(1, 20, region: "NA");

        // Assert
        result.Items.Should().HaveCount(1);
        result.TotalCount.Should().Be(1);
        result.Items.First().Region.Should().Be("NA");
    }

    [Fact]
    public async Task GetAllAsync_ShouldFilterByStatus()
    {
        // Arrange
        _context.Teams.AddRange(
            new Team
            {
                Id = Guid.NewGuid(),
                Name = "Recruiting Team",
                MaxMembers = 5,
                CurrentMemberCount = 0,
                Status = TeamStatus.Recruiting
            },
            new Team
            {
                Id = Guid.NewGuid(),
                Name = "Full Team",
                MaxMembers = 5,
                CurrentMemberCount = 5,
                Status = TeamStatus.Full
            },
            new Team
            {
                Id = Guid.NewGuid(),
                Name = "Active Team",
                MaxMembers = 5,
                CurrentMemberCount = 2,
                Status = TeamStatus.Active
            }
        );

        await _context.SaveChangesAsync();

        // Act
        var result = await _teamService.GetAllAsync(1, 20, status: TeamStatus.Recruiting);

        // Assert
        result.Items.Should().HaveCount(1);
        result.TotalCount.Should().Be(1);
        result.Items.First().Status.Should().Be(TeamStatus.Recruiting);
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnEmpty_WhenNoTeamsMatch()
    {
        // Arrange
        _context.Teams.Add(new Team
        {
            Id = Guid.NewGuid(),
            Name = "NA Team",
            MaxMembers = 5,
            CurrentMemberCount = 0,
            Status = TeamStatus.Active,
            Region = "NA"
        });

        await _context.SaveChangesAsync();

        // Act
        var result = await _teamService.GetAllAsync(1, 20, region: "SEA");

        // Assert
        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task GetAllAsync_ShouldCombineFilters_CategoryAndRegion()
    {
        // Arrange
        _context.Teams.AddRange(
            new Team
            {
                Id = Guid.NewGuid(),
                Name = "Gaming NA",
                MaxMembers = 5,
                CurrentMemberCount = 0,
                Status = TeamStatus.Recruiting,
                Category = "Gaming",
                Region = "NA"
            },
            new Team
            {
                Id = Guid.NewGuid(),
                Name = "Gaming EU",
                MaxMembers = 5,
                CurrentMemberCount = 0,
                Status = TeamStatus.Recruiting,
                Category = "Gaming",
                Region = "EU"
            },
            new Team
            {
                Id = Guid.NewGuid(),
                Name = "Sports NA",
                MaxMembers = 5,
                CurrentMemberCount = 0,
                Status = TeamStatus.Recruiting,
                Category = "Sports",
                Region = "NA"
            }
        );

        await _context.SaveChangesAsync();

        // Act
        var result = await _teamService.GetAllAsync(
            1,
            20,
            category: "Gaming",
            region: "NA");

        // Assert
        result.Items.Should().HaveCount(1);
        result.TotalCount.Should().Be(1);
        result.Items.First().Name.Should().Be("Gaming NA");
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
    public async Task UpdateAsync_ShouldOnlyUpdateProvidedFields()
    {
        // Arrange
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Original Name",
            Description = "Original Description",
            MaxMembers = 10,
            Status = TeamStatus.Recruiting,
            CurrentMemberCount = 0,
            Region = "NA",
            Category = "Gaming"
        };

        _context.Teams.Add(team);
        await _context.SaveChangesAsync();

        var updateDto = new UpdateTeamDto
        {
            Name = "Updated Name"
        };

        // Act
        var result = await _teamService.UpdateAsync(team.Id, updateDto);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Updated Name");
        result.Description.Should().Be("Original Description");
        result.Status.Should().Be(TeamStatus.Recruiting);
        result.Region.Should().Be("NA");
        result.Category.Should().Be("Gaming");
    }

    [Fact]
    public async Task UpdateAsync_ShouldNotUpdateName_WhenWhitespaceProvided()
    {
        // Arrange
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Original Name",
            MaxMembers = 5,
            Status = TeamStatus.Recruiting,
            CurrentMemberCount = 0
        };

        _context.Teams.Add(team);
        await _context.SaveChangesAsync();

        var updateDto = new UpdateTeamDto
        {
            Name = "   "
        };

        // Act
        var result = await _teamService.UpdateAsync(team.Id, updateDto);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Original Name");
    }

    [Fact]
    public async Task UpdateAsync_ShouldReturnNull_WhenNotExists()
    {
        // Arrange
        var updateDto = new UpdateTeamDto
        {
            Name = "Ghost Team"
        };

        // Act
        var result = await _teamService.UpdateAsync(Guid.NewGuid(), updateDto);

        // Assert
        result.Should().BeNull();
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
    public async Task DeleteAsync_ShouldReturnFalse_WhenNotExists()
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