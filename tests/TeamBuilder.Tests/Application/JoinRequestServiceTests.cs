using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Application;

public class JoinRequestServiceTests : IDisposable
{
    private readonly TeamBuilderDbContext _context;
    private readonly JoinRequestService _joinRequestService;

    public JoinRequestServiceTests()
    {
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TeamBuilderDbContext(options);
        _joinRequestService = new JoinRequestService(_context);
    }

    [Fact]
    public async Task CreateAsync_ShouldCreateJoinRequest_Successfully()
    {
        // Arrange
        var team = new Team { Id = Guid.NewGuid(), Name = "Test Team", MaxMembers = 5 };
        var player = new Player { Id = Guid.NewGuid(), Username = "TestPlayer" };
        _context.Teams.Add(team);
        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        var createDto = new CreateJoinRequestDto
        {
            TeamId = team.Id,
            Message = "I want to join!"
        };

        // Act
        var result = await _joinRequestService.CreateAsync(createDto, player.Id);

        // Assert
        result.Should().NotBeNull();
        result.TeamId.Should().Be(team.Id);
        result.PlayerId.Should().Be(player.Id);
        result.Status.Should().Be(RequestStatus.Pending);
        result.Message.Should().Be("I want to join!");
    }

    [Fact]
    public async Task CreateAsync_ShouldThrow_WhenPendingRequestExists()
    {
        // Arrange
        var team = new Team { Id = Guid.NewGuid(), Name = "Test Team", MaxMembers = 5 };
        var player = new Player { Id = Guid.NewGuid(), Username = "TestPlayer" };
        _context.Teams.Add(team);
        _context.Players.Add(player);

        var existingRequest = new JoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            PlayerId = player.Id,
            Status = RequestStatus.Pending,
            RequestedAtUtc = DateTime.UtcNow
        };
        _context.JoinRequests.Add(existingRequest);
        await _context.SaveChangesAsync();

        var createDto = new CreateJoinRequestDto { TeamId = team.Id };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _joinRequestService.CreateAsync(createDto, player.Id));
    }

    [Fact]
    public async Task ProcessAsync_ShouldApproveAndCreateTeamMember()
    {
        // Arrange
        var team = new Team 
        { 
            Id = Guid.NewGuid(), 
            Name = "Test Team", 
            MaxMembers = 5,
            CurrentMemberCount = 2 
        };
        var player = new Player { Id = Guid.NewGuid(), Username = "TestPlayer" };
        _context.Teams.Add(team);
        _context.Players.Add(player);

        var joinRequest = new JoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            PlayerId = player.Id,
            Status = RequestStatus.Pending,
            RequestedAtUtc = DateTime.UtcNow
        };
        _context.JoinRequests.Add(joinRequest);
        await _context.SaveChangesAsync();

        var processDto = new ProcessJoinRequestDto { Status = RequestStatus.Approved };

        // Act
        var result = await _joinRequestService.ProcessAsync(joinRequest.Id, processDto, Guid.NewGuid());

        // Assert
        result.Should().NotBeNull();
        result!.Status.Should().Be(RequestStatus.Approved);
        result.ProcessedAtUtc.Should().NotBeNull();

        var teamMember = await _context.TeamMembers
            .FirstOrDefaultAsync(tm => tm.TeamId == team.Id && tm.PlayerId == player.Id);
        teamMember.Should().NotBeNull();
        teamMember!.IsActive.Should().BeTrue();
        teamMember.Role.Should().Be(TeamRole.Member);

        var updatedTeam = await _context.Teams.FindAsync(team.Id);
        updatedTeam!.CurrentMemberCount.Should().Be(3);
    }

    [Fact]
    public async Task ProcessAsync_ShouldReject_WithoutCreatingTeamMember()
    {
        // Arrange
        var team = new Team { Id = Guid.NewGuid(), Name = "Test Team", MaxMembers = 5 };
        var player = new Player { Id = Guid.NewGuid(), Username = "TestPlayer" };
        _context.Teams.Add(team);
        _context.Players.Add(player);

        var joinRequest = new JoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            PlayerId = player.Id,
            Status = RequestStatus.Pending,
            RequestedAtUtc = DateTime.UtcNow
        };
        _context.JoinRequests.Add(joinRequest);
        await _context.SaveChangesAsync();

        var processDto = new ProcessJoinRequestDto { Status = RequestStatus.Rejected };

        // Act
        var result = await _joinRequestService.ProcessAsync(joinRequest.Id, processDto, Guid.NewGuid());

        // Assert
        result.Should().NotBeNull();
        result!.Status.Should().Be(RequestStatus.Rejected);

        var teamMember = await _context.TeamMembers
            .FirstOrDefaultAsync(tm => tm.TeamId == team.Id && tm.PlayerId == player.Id);
        teamMember.Should().BeNull();
    }

    [Fact]
    public async Task ProcessAsync_ShouldMarkTeamAsFull_WhenReachingMaxMembers()
    {
        // Arrange
        var team = new Team 
        { 
            Id = Guid.NewGuid(), 
            Name = "Test Team", 
            MaxMembers = 3,
            CurrentMemberCount = 2,
            Status = TeamStatus.Recruiting
        };
        var player = new Player { Id = Guid.NewGuid(), Username = "TestPlayer" };
        _context.Teams.Add(team);
        _context.Players.Add(player);

        var joinRequest = new JoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            PlayerId = player.Id,
            Status = RequestStatus.Pending,
            RequestedAtUtc = DateTime.UtcNow
        };
        _context.JoinRequests.Add(joinRequest);
        await _context.SaveChangesAsync();

        var processDto = new ProcessJoinRequestDto { Status = RequestStatus.Approved };

        // Act
        await _joinRequestService.ProcessAsync(joinRequest.Id, processDto, Guid.NewGuid());

        // Assert
        var updatedTeam = await _context.Teams.FindAsync(team.Id);
        updatedTeam!.CurrentMemberCount.Should().Be(3);
        updatedTeam.Status.Should().Be(TeamStatus.Full);
    }

    [Fact]
    public async Task ProcessAsync_ShouldThrow_WhenAlreadyProcessed()
    {
        // Arrange
        var team = new Team { Id = Guid.NewGuid(), Name = "Test Team", MaxMembers = 5 };
        var player = new Player { Id = Guid.NewGuid(), Username = "TestPlayer" };
        _context.Teams.Add(team);
        _context.Players.Add(player);

        var joinRequest = new JoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            PlayerId = player.Id,
            Status = RequestStatus.Approved,
            RequestedAtUtc = DateTime.UtcNow,
            ProcessedAtUtc = DateTime.UtcNow
        };
        _context.JoinRequests.Add(joinRequest);
        await _context.SaveChangesAsync();

        var processDto = new ProcessJoinRequestDto { Status = RequestStatus.Approved };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _joinRequestService.ProcessAsync(joinRequest.Id, processDto, Guid.NewGuid()));
    }

    [Fact]
    public async Task GetByTeamIdAsync_ShouldReturnFilteredRequests()
    {
        // Arrange
        var team = new Team { Id = Guid.NewGuid(), Name = "Test Team", MaxMembers = 5 };
        _context.Teams.Add(team);

        for (int i = 0; i < 10; i++)
        {
            var player = new Player { Id = Guid.NewGuid(), Username = $"Player{i}" };
            _context.Players.Add(player);

            _context.JoinRequests.Add(new JoinRequest
            {
                Id = Guid.NewGuid(),
                TeamId = team.Id,
                PlayerId = player.Id,
                Status = i < 5 ? RequestStatus.Pending : RequestStatus.Approved,
                RequestedAtUtc = DateTime.UtcNow.AddDays(-i)
            });
        }
        await _context.SaveChangesAsync();

        // Act
        var result = await _joinRequestService.GetByTeamIdAsync(team.Id, 1, 20, RequestStatus.Pending);

        // Assert
        result.Items.Should().HaveCount(5);
        result.Items.Should().OnlyContain(jr => jr.Status == RequestStatus.Pending);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnJoinRequest_WhenExists()
    {
        // Arrange
        var team = new Team { Id = Guid.NewGuid(), Name = "Team", MaxMembers = 5 };
        var player = new Player { Id = Guid.NewGuid(), Username = "Requester" };
        _context.Teams.Add(team);
        _context.Players.Add(player);

        var joinRequest = new JoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            PlayerId = player.Id,
            Status = RequestStatus.Pending,
            Message = "Let me in",
            RequestedAtUtc = DateTime.UtcNow
        };
        _context.JoinRequests.Add(joinRequest);
        await _context.SaveChangesAsync();

        // Act
        var result = await _joinRequestService.GetByIdAsync(joinRequest.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(joinRequest.Id);
        result.Status.Should().Be(RequestStatus.Pending);
        result.Message.Should().Be("Let me in");
        result.TeamName.Should().Be("Team");
        result.PlayerUsername.Should().Be("Requester");
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_WhenNotExists()
    {
        // Act
        var result = await _joinRequestService.GetByIdAsync(Guid.NewGuid());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByPlayerIdAsync_ShouldReturnFilteredRequests()
    {
        // Arrange
        var player = new Player { Id = Guid.NewGuid(), Username = "ActiveRequester" };
        _context.Players.Add(player);

        for (int i = 0; i < 4; i++)
        {
            var team = new Team { Id = Guid.NewGuid(), Name = $"Team{i}", MaxMembers = 5 };
            _context.Teams.Add(team);
            _context.JoinRequests.Add(new JoinRequest
            {
                Id = Guid.NewGuid(),
                TeamId = team.Id,
                PlayerId = player.Id,
                Status = i < 2 ? RequestStatus.Pending : RequestStatus.Rejected,
                RequestedAtUtc = DateTime.UtcNow.AddDays(-i)
            });
        }
        await _context.SaveChangesAsync();

        // Act
        var result = await _joinRequestService.GetByPlayerIdAsync(player.Id, 1, 20, RequestStatus.Pending);

        // Assert
        result.Items.Should().HaveCount(2);
        result.Items.Should().OnlyContain(jr => jr.Status == RequestStatus.Pending);
    }

    [Fact]
    public async Task ProcessAsync_ShouldReturnNull_WhenJoinRequestNotFound()
    {
        // Arrange
        var processDto = new ProcessJoinRequestDto { Status = RequestStatus.Approved };

        // Act
        var result = await _joinRequestService.ProcessAsync(Guid.NewGuid(), processDto, Guid.NewGuid());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByPlayerIdAsync_ShouldReturnAllRequests_WhenNoStatusFilter()
    {
        // Arrange
        var player = new Player { Id = Guid.NewGuid(), Username = "MultiStatusPlayer" };
        _context.Players.Add(player);

        foreach (var status in new[] { RequestStatus.Pending, RequestStatus.Approved, RequestStatus.Rejected })
        {
            var team = new Team { Id = Guid.NewGuid(), Name = $"Team-{status}", MaxMembers = 5 };
            _context.Teams.Add(team);
            _context.JoinRequests.Add(new JoinRequest
            {
                Id = Guid.NewGuid(),
                TeamId = team.Id,
                PlayerId = player.Id,
                Status = status,
                RequestedAtUtc = DateTime.UtcNow
            });
        }
        await _context.SaveChangesAsync();

        // Act
        var result = await _joinRequestService.GetByPlayerIdAsync(player.Id, 1, 20);

        // Assert
        result.TotalCount.Should().Be(3);
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
