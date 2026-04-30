using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Application;

public class TeamMembershipTests : IDisposable
{
    private readonly TeamBuilderDbContext _context;
    private readonly TeamService _teamService;

    public TeamMembershipTests()
    {
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TeamBuilderDbContext(options);
        _teamService = new TeamService(_context);
    }

    [Fact]
    public async Task RemoveMemberAsync_ShouldMarkMemberAsInactive()
    {
        // Arrange
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Team",
            MaxMembers = 5,
            CurrentMemberCount = 3,
            Status = TeamStatus.Active,
            RowVersion = []
        };
        var player = new Player { Id = Guid.NewGuid(), Username = "TestPlayer" };
        _context.Teams.Add(team);
        _context.Players.Add(player);

        var teamMember = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            PlayerId = player.Id,
            Role = TeamRole.Member,
            JoinedAtUtc = DateTime.UtcNow.AddDays(-10),
            IsActive = true
        };
        _context.TeamMembers.Add(teamMember);
        await _context.SaveChangesAsync();

        // Act
        var result = await _teamService.RemoveMemberAsync(team.Id, player.Id);

        // Assert
        result.Should().BeTrue();

        var updatedMember = await _context.TeamMembers.FindAsync(teamMember.Id);
        updatedMember!.IsActive.Should().BeFalse();

        var updatedTeam = await _context.Teams.FindAsync(team.Id);
        updatedTeam!.CurrentMemberCount.Should().Be(2);
    }

    [Fact]
    public async Task RemoveMemberAsync_ShouldChangeStatusToRecruiting_WhenTeamWasFull()
    {
        // Arrange
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Team",
            MaxMembers = 3,
            CurrentMemberCount = 3,
            Status = TeamStatus.Full
        };
        var player = new Player { Id = Guid.NewGuid(), Username = "TestPlayer", RowVersion = [] };
        _context.Teams.Add(team);
        _context.Players.Add(player);

        var teamMember = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            PlayerId = player.Id,
            Role = TeamRole.Member,
            JoinedAtUtc = DateTime.UtcNow,
            IsActive = true
        };
        _context.TeamMembers.Add(teamMember);
        await _context.SaveChangesAsync();

        // Act
        var result = await _teamService.RemoveMemberAsync(team.Id, player.Id);

        // Assert
        result.Should().BeTrue();

        var updatedTeam = await _context.Teams.FindAsync(team.Id);
        updatedTeam!.CurrentMemberCount.Should().Be(2);
        updatedTeam.Status.Should().Be(TeamStatus.Recruiting);
    }

    [Fact]
    public async Task RemoveMemberAsync_ShouldReturnFalse_WhenMemberNotFound()
    {
        // Arrange
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Team",
            MaxMembers = 5,
            CurrentMemberCount = 3,
            RowVersion = []
        };
        _context.Teams.Add(team);
        await _context.SaveChangesAsync();

        var nonExistentPlayerId = Guid.NewGuid();

        // Act
        var result = await _teamService.RemoveMemberAsync(team.Id, nonExistentPlayerId);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveMemberAsync_ShouldReturnFalse_WhenMemberAlreadyInactive()
    {
        // Arrange
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Team",
            MaxMembers = 5,
            CurrentMemberCount = 2,
            RowVersion = []
        };
        var player = new Player { Id = Guid.NewGuid(), Username = "TestPlayer", RowVersion = [] };
        _context.Teams.Add(team);
        _context.Players.Add(player);

        var teamMember = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            PlayerId = player.Id,
            Role = TeamRole.Member,
            JoinedAtUtc = DateTime.UtcNow.AddDays(-10),
            IsActive = false // Already inactive
        };
        _context.TeamMembers.Add(teamMember);
        await _context.SaveChangesAsync();

        // Act
        var result = await _teamService.RemoveMemberAsync(team.Id, player.Id);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task RefillScenario_JoinAfterLeave_ShouldWork()
    {
        // Arrange - Create a full team
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Team",
            MaxMembers = 3,
            CurrentMemberCount = 3,
            Status = TeamStatus.Full,
            RowVersion = []
        };
        var leavingPlayer = new Player { Id = Guid.NewGuid(), Username = "LeavingPlayer", RowVersion = [] };
        var newPlayer = new Player { Id = Guid.NewGuid(), Username = "NewPlayer", RowVersion = [] };

        _context.Teams.Add(team);
        _context.Players.AddRange(leavingPlayer, newPlayer);

        var teamMember = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            PlayerId = leavingPlayer.Id,
            Role = TeamRole.Member,
            JoinedAtUtc = DateTime.UtcNow,
            IsActive = true
        };
        _context.TeamMembers.Add(teamMember);
        await _context.SaveChangesAsync();

        // Act - Player leaves
        var leaveResult = await _teamService.RemoveMemberAsync(team.Id, leavingPlayer.Id);

        // Assert - Team should be recruiting again
        leaveResult.Should().BeTrue();
        var teamAfterLeave = await _context.Teams.FindAsync(team.Id);
        teamAfterLeave!.Status.Should().Be(TeamStatus.Recruiting);
        teamAfterLeave.CurrentMemberCount.Should().Be(2);

        // Act - New player can now join (via join request processing)
        var joinRequest = new JoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            PlayerId = newPlayer.Id,
            Status = RequestStatus.Pending,
            RequestedAtUtc = DateTime.UtcNow
        };
        _context.JoinRequests.Add(joinRequest);
        await _context.SaveChangesAsync();

        var joinRequestService = new JoinRequestService(_context);
        var processDto = new TeamBuilder.Application.DTOs.ProcessJoinRequestDto { Status = RequestStatus.Approved };
        await joinRequestService.ProcessAsync(joinRequest.Id, processDto, Guid.NewGuid());

        // Assert - New member should be added
        var teamAfterRefill = await _context.Teams.FindAsync(team.Id);
        teamAfterRefill!.CurrentMemberCount.Should().Be(3);
        teamAfterRefill.Status.Should().Be(TeamStatus.Full);

        var newMember = await _context.TeamMembers
            .FirstOrDefaultAsync(tm => tm.PlayerId == newPlayer.Id && tm.TeamId == team.Id && tm.IsActive);
        newMember.Should().NotBeNull();
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}