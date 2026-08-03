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
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Team",
            MaxMembers = 5
        };

        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = "TestPlayer"
        };

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
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Team",
            MaxMembers = 5
        };

        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = "TestPlayer"
        };

        _context.Teams.Add(team);
        _context.Players.Add(player);

        _context.JoinRequests.Add(new JoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            PlayerId = player.Id,
            Status = RequestStatus.Pending,
            RequestedAtUtc = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();

        var createDto = new CreateJoinRequestDto
        {
            TeamId = team.Id
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _joinRequestService.CreateAsync(createDto, player.Id));
    }

    [Fact]
    public async Task CreateAsync_ShouldSucceed_WhenPreviousRequestWasRejected()
    {
        // Arrange
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Team",
            MaxMembers = 5
        };

        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = "TestPlayer"
        };

        _context.Teams.Add(team);
        _context.Players.Add(player);

        _context.JoinRequests.Add(new JoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            PlayerId = player.Id,
            Status = RequestStatus.Rejected,
            RequestedAtUtc = DateTime.UtcNow.AddDays(-1),
            ProcessedAtUtc = DateTime.UtcNow.AddDays(-1)
        });

        await _context.SaveChangesAsync();

        var createDto = new CreateJoinRequestDto
        {
            TeamId = team.Id,
            Message = "Trying again"
        };

        // Act
        var result = await _joinRequestService.CreateAsync(createDto, player.Id);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(RequestStatus.Pending);
        result.Message.Should().Be("Trying again");
    }

    [Fact]
    public async Task CreateAsync_ShouldSucceed_WhenPreviousRequestWasCancelled()
    {
        // Arrange
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Team",
            MaxMembers = 5
        };

        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = "TestPlayer"
        };

        _context.Teams.Add(team);
        _context.Players.Add(player);

        _context.JoinRequests.Add(new JoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            PlayerId = player.Id,
            Status = RequestStatus.Cancelled,
            RequestedAtUtc = DateTime.UtcNow.AddDays(-1)
        });

        await _context.SaveChangesAsync();

        var createDto = new CreateJoinRequestDto
        {
            TeamId = team.Id
        };

        // Act
        var result = await _joinRequestService.CreateAsync(createDto, player.Id);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(RequestStatus.Pending);
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

        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = "TestPlayer"
        };

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

        var processDto = new ProcessJoinRequestDto
        {
            Status = RequestStatus.Approved
        };

        // Act
        var result = await _joinRequestService.ProcessAsync(
            joinRequest.Id,
            processDto,
            Guid.NewGuid());

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
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Team",
            MaxMembers = 5
        };

        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = "TestPlayer"
        };

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

        var processDto = new ProcessJoinRequestDto
        {
            Status = RequestStatus.Rejected
        };

        // Act
        var result = await _joinRequestService.ProcessAsync(
            joinRequest.Id,
            processDto,
            Guid.NewGuid());

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

        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = "TestPlayer"
        };

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

        var processDto = new ProcessJoinRequestDto
        {
            Status = RequestStatus.Approved
        };

        // Act
        await _joinRequestService.ProcessAsync(
            joinRequest.Id,
            processDto,
            Guid.NewGuid());

        // Assert
        var updatedTeam = await _context.Teams.FindAsync(team.Id);
        updatedTeam!.CurrentMemberCount.Should().Be(3);
        updatedTeam.Status.Should().Be(TeamStatus.Full);
    }

    [Fact]
    public async Task ProcessAsync_ShouldThrowConflict_WhenApprovingFullTeam()
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

        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = "TestPlayer"
        };

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

        var processDto = new ProcessJoinRequestDto
        {
            Status = RequestStatus.Approved
        };

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _joinRequestService.ProcessAsync(joinRequest.Id, processDto, Guid.NewGuid()));

        // Assert
        exception.Message.Should().Contain("already full");
    }

    [Fact]
    public async Task ProcessAsync_ShouldAllowRejection_WhenTeamIsFull()
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

        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = "TestPlayer"
        };

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

        var processDto = new ProcessJoinRequestDto
        {
            Status = RequestStatus.Rejected
        };

        // Act
        var result = await _joinRequestService.ProcessAsync(
            joinRequest.Id,
            processDto,
            Guid.NewGuid());

        // Assert
        result.Should().NotBeNull();
        result!.Status.Should().Be(RequestStatus.Rejected);

        var savedRequest = await _context.JoinRequests.FindAsync(joinRequest.Id);
        savedRequest!.Status.Should().Be(RequestStatus.Rejected);

        var teamMember = await _context.TeamMembers
            .FirstOrDefaultAsync(tm => tm.TeamId == team.Id && tm.PlayerId == player.Id);

        teamMember.Should().BeNull();
    }

    [Fact]
    public async Task ProcessAsync_ShouldThrowConflict_WhenSaveHitsConcurrencyException()
    {
        // Arrange
        var databaseName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        using (var seedContext = new TeamBuilderDbContext(options))
        {
            var team = new Team
            {
                Id = Guid.NewGuid(),
                Name = "Test Team",
                MaxMembers = 5,
                CurrentMemberCount = 2,
                Status = TeamStatus.Recruiting
            };

            var player = new Player
            {
                Id = Guid.NewGuid(),
                Username = "TestPlayer"
            };

            seedContext.Teams.Add(team);
            seedContext.Players.Add(player);
            seedContext.JoinRequests.Add(new JoinRequest
            {
                Id = Guid.NewGuid(),
                TeamId = team.Id,
                PlayerId = player.Id,
                Status = RequestStatus.Pending,
                RequestedAtUtc = DateTime.UtcNow
            });
            await seedContext.SaveChangesAsync();
        }

        using var conflictContext = new ThrowingConcurrencyTeamBuilderDbContext(options);
        var service = new JoinRequestService(conflictContext);

        var joinRequest = await conflictContext.JoinRequests.FirstAsync();
        var processDto = new ProcessJoinRequestDto
        {
            Status = RequestStatus.Approved
        };

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ProcessAsync(joinRequest.Id, processDto, Guid.NewGuid()));

        // Assert
        exception.Message.Should().Contain("changed while this join request was being processed");
    }

    [Fact]
    public async Task ProcessAsync_ShouldThrowConflict_WhenPlayerIsAlreadyAnActiveMember()
    {
        // Arrange
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Team",
            MaxMembers = 5,
            CurrentMemberCount = 1
        };

        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = "TestPlayer"
        };

        _context.Teams.Add(team);
        _context.Players.Add(player);
        _context.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            PlayerId = player.Id,
            Role = TeamRole.Member,
            JoinedAtUtc = DateTime.UtcNow,
            IsActive = true
        });

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

        var processDto = new ProcessJoinRequestDto
        {
            Status = RequestStatus.Approved
        };

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _joinRequestService.ProcessAsync(joinRequest.Id, processDto, Guid.NewGuid()));

        // Assert
        exception.Message.Should().Contain("already an active member of this team");

        var memberCount = await _context.TeamMembers
            .CountAsync(tm => tm.TeamId == team.Id && tm.PlayerId == player.Id);
        memberCount.Should().Be(1);
    }

    [Fact]
    public async Task ProcessAsync_ShouldThrowConflict_WhenSaveHitsRecognizedDuplicateMembershipRace()
    {
        // Arrange
        var databaseName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        using (var seedContext = new TeamBuilderDbContext(options))
        {
            var team = new Team
            {
                Id = Guid.NewGuid(),
                Name = "Test Team",
                MaxMembers = 5,
                CurrentMemberCount = 1,
                Status = TeamStatus.Recruiting
            };

            var player = new Player
            {
                Id = Guid.NewGuid(),
                Username = "TestPlayer"
            };

            seedContext.Teams.Add(team);
            seedContext.Players.Add(player);
            seedContext.JoinRequests.Add(new JoinRequest
            {
                Id = Guid.NewGuid(),
                TeamId = team.Id,
                PlayerId = player.Id,
                Status = RequestStatus.Pending,
                RequestedAtUtc = DateTime.UtcNow
            });
            await seedContext.SaveChangesAsync();
        }

        using var raceContext = new ThrowingDuplicateMembershipTeamBuilderDbContext(options);
        var service = new JoinRequestService(raceContext);

        var joinRequest = await raceContext.JoinRequests.FirstAsync();
        var processDto = new ProcessJoinRequestDto
        {
            Status = RequestStatus.Approved
        };

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ProcessAsync(joinRequest.Id, processDto, Guid.NewGuid()));

        // Assert
        exception.Message.Should().Contain("already an active member of this team");
    }

    [Fact]
    public async Task ProcessAsync_ShouldNotMislabel_UnrelatedDbUpdateException()
    {
        // Arrange
        var databaseName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        using (var seedContext = new TeamBuilderDbContext(options))
        {
            var team = new Team
            {
                Id = Guid.NewGuid(),
                Name = "Test Team",
                MaxMembers = 5,
                CurrentMemberCount = 1,
                Status = TeamStatus.Recruiting
            };

            var player = new Player
            {
                Id = Guid.NewGuid(),
                Username = "TestPlayer"
            };

            seedContext.Teams.Add(team);
            seedContext.Players.Add(player);
            seedContext.JoinRequests.Add(new JoinRequest
            {
                Id = Guid.NewGuid(),
                TeamId = team.Id,
                PlayerId = player.Id,
                Status = RequestStatus.Pending,
                RequestedAtUtc = DateTime.UtcNow
            });
            await seedContext.SaveChangesAsync();
        }

        using var unrelatedFailureContext = new ThrowingUnrelatedDbUpdateExceptionTeamBuilderDbContext(options);
        var service = new JoinRequestService(unrelatedFailureContext);

        var joinRequest = await unrelatedFailureContext.JoinRequests.FirstAsync();
        var processDto = new ProcessJoinRequestDto
        {
            Status = RequestStatus.Approved
        };

        // Act
        var act = () => service.ProcessAsync(joinRequest.Id, processDto, Guid.NewGuid());

        // Assert: the unrecognized DbUpdateException must propagate as-is, not be
        // reinterpreted as a duplicate-membership conflict.
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task ProcessAsync_ShouldThrow_WhenAlreadyProcessed()
    {
        // Arrange
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Team",
            MaxMembers = 5
        };

        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = "TestPlayer"
        };

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

        var processDto = new ProcessJoinRequestDto
        {
            Status = RequestStatus.Approved
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _joinRequestService.ProcessAsync(joinRequest.Id, processDto, Guid.NewGuid()));
    }

    [Fact]
    public async Task ProcessAsync_ShouldReturnNull_WhenRequestNotFound()
    {
        // Arrange
        var processDto = new ProcessJoinRequestDto
        {
            Status = RequestStatus.Approved
        };

        // Act
        var result = await _joinRequestService.ProcessAsync(
            Guid.NewGuid(),
            processDto,
            Guid.NewGuid());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task ProcessAsync_ShouldCancel_WithoutCreatingTeamMember()
    {
        // Arrange
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Team",
            MaxMembers = 5,
            CurrentMemberCount = 1
        };

        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = "CancelPlayer"
        };

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

        var processDto = new ProcessJoinRequestDto
        {
            Status = RequestStatus.Cancelled
        };

        // Act
        var result = await _joinRequestService.ProcessAsync(
            joinRequest.Id,
            processDto,
            Guid.NewGuid());

        // Assert
        result.Should().NotBeNull();
        result!.Status.Should().Be(RequestStatus.Cancelled);

        var teamMember = await _context.TeamMembers
            .FirstOrDefaultAsync(tm => tm.TeamId == team.Id && tm.PlayerId == player.Id);

        teamMember.Should().BeNull();

        var updatedTeam = await _context.Teams.FindAsync(team.Id);
        updatedTeam!.CurrentMemberCount.Should().Be(1);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnJoinRequest_WhenExists()
    {
        // Arrange
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Team",
            MaxMembers = 5
        };

        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = "TestPlayer"
        };

        _context.Teams.Add(team);
        _context.Players.Add(player);

        var joinRequest = new JoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            PlayerId = player.Id,
            Status = RequestStatus.Pending,
            Message = "Please let me in",
            RequestedAtUtc = DateTime.UtcNow
        };

        _context.JoinRequests.Add(joinRequest);
        await _context.SaveChangesAsync();

        // Act
        var result = await _joinRequestService.GetByIdAsync(joinRequest.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(joinRequest.Id);
        result.TeamId.Should().Be(team.Id);
        result.PlayerId.Should().Be(player.Id);
        result.Status.Should().Be(RequestStatus.Pending);
        result.Message.Should().Be("Please let me in");
        result.TeamName.Should().Be("Test Team");
        result.PlayerUsername.Should().Be("TestPlayer");
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
    public async Task GetByPlayerIdAsync_ShouldReturnPaginatedRequests()
    {
        // Arrange
        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = "TestPlayer"
        };

        _context.Players.Add(player);

        for (var i = 0; i < 5; i++)
        {
            var team = new Team
            {
                Id = Guid.NewGuid(),
                Name = $"Team{i}",
                MaxMembers = 5
            };

            _context.Teams.Add(team);
            _context.JoinRequests.Add(new JoinRequest
            {
                Id = Guid.NewGuid(),
                TeamId = team.Id,
                PlayerId = player.Id,
                Status = RequestStatus.Pending,
                RequestedAtUtc = DateTime.UtcNow.AddMinutes(-i)
            });
        }

        await _context.SaveChangesAsync();

        // Act
        var result = await _joinRequestService.GetByPlayerIdAsync(player.Id, 1, 3);

        // Assert
        result.Should().NotBeNull();
        result.Items.Should().HaveCount(3);
        result.TotalCount.Should().Be(5);
        result.TotalPages.Should().Be(2);
        result.HasNextPage.Should().BeTrue();
        result.HasPreviousPage.Should().BeFalse();
        result.Items.Should().OnlyContain(jr => jr.PlayerId == player.Id);
    }

    [Fact]
    public async Task GetByPlayerIdAsync_ShouldFilterByStatus()
    {
        // Arrange
        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = "TestPlayer"
        };

        _context.Players.Add(player);

        for (var i = 0; i < 6; i++)
        {
            var team = new Team
            {
                Id = Guid.NewGuid(),
                Name = $"Team{i}",
                MaxMembers = 5
            };

            _context.Teams.Add(team);
            _context.JoinRequests.Add(new JoinRequest
            {
                Id = Guid.NewGuid(),
                TeamId = team.Id,
                PlayerId = player.Id,
                Status = i < 4 ? RequestStatus.Pending : RequestStatus.Approved,
                RequestedAtUtc = DateTime.UtcNow.AddMinutes(-i)
            });
        }

        await _context.SaveChangesAsync();

        // Act
        var result = await _joinRequestService.GetByPlayerIdAsync(
            player.Id,
            1,
            20,
            RequestStatus.Pending);

        // Assert
        result.Items.Should().HaveCount(4);
        result.Items.Should().OnlyContain(jr => jr.Status == RequestStatus.Pending);
    }

    [Fact]
    public async Task GetByPlayerIdAsync_ShouldReturnAllRequests_WhenNoStatusFilter()
    {
        // Arrange
        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = "MultiStatusPlayer"
        };

        _context.Players.Add(player);

        foreach (var status in new[] { RequestStatus.Pending, RequestStatus.Approved, RequestStatus.Rejected })
        {
            var team = new Team
            {
                Id = Guid.NewGuid(),
                Name = $"Team-{status}",
                MaxMembers = 5
            };

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
        result.Items.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetByPlayerIdAsync_ShouldReturnEmpty_WhenPlayerHasNoRequests()
    {
        // Arrange
        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = "Loner"
        };

        _context.Players.Add(player);
        await _context.SaveChangesAsync();

        // Act
        var result = await _joinRequestService.GetByPlayerIdAsync(player.Id, 1, 20);

        // Assert
        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task GetByTeamIdAsync_ShouldReturnFilteredRequests()
    {
        // Arrange
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Team",
            MaxMembers = 5
        };

        _context.Teams.Add(team);

        for (var i = 0; i < 10; i++)
        {
            var player = new Player
            {
                Id = Guid.NewGuid(),
                Username = $"Player{i}"
            };

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
        var result = await _joinRequestService.GetByTeamIdAsync(
            team.Id,
            1,
            20,
            RequestStatus.Pending);

        // Assert
        result.Items.Should().HaveCount(5);
        result.Items.Should().OnlyContain(jr => jr.Status == RequestStatus.Pending);
    }

    [Fact]
    public async Task GetByTeamIdAsync_ShouldReturnPaginatedResults()
    {
        // Arrange
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Team",
            MaxMembers = 20
        };

        _context.Teams.Add(team);

        for (var i = 0; i < 7; i++)
        {
            var player = new Player
            {
                Id = Guid.NewGuid(),
                Username = $"PlayerT{i}"
            };

            _context.Players.Add(player);
            _context.JoinRequests.Add(new JoinRequest
            {
                Id = Guid.NewGuid(),
                TeamId = team.Id,
                PlayerId = player.Id,
                Status = RequestStatus.Pending,
                RequestedAtUtc = DateTime.UtcNow.AddMinutes(-i)
            });
        }

        await _context.SaveChangesAsync();

        // Act
        var result = await _joinRequestService.GetByTeamIdAsync(team.Id, 1, 5);

        // Assert
        result.Items.Should().HaveCount(5);
        result.TotalCount.Should().Be(7);
        result.TotalPages.Should().Be(2);
        result.HasNextPage.Should().BeTrue();
        result.HasPreviousPage.Should().BeFalse();
        result.Items.Should().OnlyContain(jr => jr.TeamId == team.Id);
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowArgumentException_WhenTeamIdIsNull()
    {
        // Arrange
        var dto = new CreateJoinRequestDto { TeamId = null };

        // Act
        var act = async () => await _joinRequestService.CreateAsync(dto, Guid.NewGuid());

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("createJoinRequestDto");
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowConflict_WhenSaveHitsRecognizedDuplicatePendingRequestRace()
    {
        // Arrange
        var databaseName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        Guid teamId;
        Guid playerId;

        using (var seedContext = new TeamBuilderDbContext(options))
        {
            var team = new Team
            {
                Id = Guid.NewGuid(),
                Name = "Test Team",
                MaxMembers = 5
            };

            var player = new Player
            {
                Id = Guid.NewGuid(),
                Username = "TestPlayer"
            };

            teamId = team.Id;
            playerId = player.Id;

            seedContext.Teams.Add(team);
            seedContext.Players.Add(player);
            await seedContext.SaveChangesAsync();
        }

        using var raceContext = new ThrowingDuplicatePendingJoinRequestTeamBuilderDbContext(options);
        var service = new JoinRequestService(raceContext);

        var createDto = new CreateJoinRequestDto
        {
            TeamId = teamId
        };

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(createDto, playerId));

        // Assert
        exception.Message.Should().Contain("A pending join request already exists for this team.");
    }

    [Fact]
    public async Task CreateAsync_ShouldNotMislabel_UnrelatedDbUpdateException()
    {
        // Arrange
        var databaseName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        Guid teamId;
        Guid playerId;

        using (var seedContext = new TeamBuilderDbContext(options))
        {
            var team = new Team
            {
                Id = Guid.NewGuid(),
                Name = "Test Team",
                MaxMembers = 5
            };

            var player = new Player
            {
                Id = Guid.NewGuid(),
                Username = "TestPlayer"
            };

            teamId = team.Id;
            playerId = player.Id;

            seedContext.Teams.Add(team);
            seedContext.Players.Add(player);
            await seedContext.SaveChangesAsync();
        }

        using var raceContext = new ThrowingUnrelatedDbUpdateExceptionOnCreateTeamBuilderDbContext(options);
        var service = new JoinRequestService(raceContext);

        var createDto = new CreateJoinRequestDto
        {
            TeamId = teamId
        };

        // Act & Assert
        await Assert.ThrowsAsync<DbUpdateException>(
            () => service.CreateAsync(createDto, playerId));
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowArgumentException_WhenTeamIdIsEmpty()
    {
        // Arrange
        var dto = new CreateJoinRequestDto { TeamId = Guid.Empty };

        // Act
        var act = async () => await _joinRequestService.CreateAsync(dto, Guid.NewGuid());

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("createJoinRequestDto");
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    private sealed class ThrowingConcurrencyTeamBuilderDbContext(DbContextOptions<TeamBuilderDbContext> options)
        : TeamBuilderDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            throw new DbUpdateConcurrencyException("Simulated concurrency conflict");
        }
    }

    /// <summary>
    /// Simulates the SQL Server error that the UX_TeamMembers_TeamId_PlayerId unique
    /// index would raise if two concurrent requests both passed the application-level
    /// duplicate check and raced to insert the same (TeamId, PlayerId) membership.
    /// </summary>
    private sealed class ThrowingDuplicateMembershipTeamBuilderDbContext(DbContextOptions<TeamBuilderDbContext> options)
        : TeamBuilderDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var sqlException = SqlExceptionTestFactory.Create(
                2627,
                "Violation of UNIQUE KEY constraint 'UX_TeamMembers_TeamId_PlayerId'. Cannot insert duplicate key in object 'dbo.TeamMembers'.");
            throw new DbUpdateException("Simulated duplicate membership race", sqlException);
        }
    }

    /// <summary>
    /// Simulates an unrelated persistence failure that must NOT be reinterpreted as a
    /// duplicate-membership conflict.
    /// </summary>
    private sealed class ThrowingUnrelatedDbUpdateExceptionTeamBuilderDbContext(DbContextOptions<TeamBuilderDbContext> options)
        : TeamBuilderDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var sqlException = SqlExceptionTestFactory.Create(
                547,
                "The INSERT statement conflicted with the FOREIGN KEY constraint.");
            throw new DbUpdateException("Simulated unrelated persistence failure", sqlException);
        }
    }

    /// <summary>
    /// Simulates the SQL Server error that the UX_JoinRequests_TeamId_PlayerId_Pending
    /// unique filtered index would raise if two concurrent requests both passed the
    /// application-level duplicate check and raced to insert the same pending
    /// (TeamId, PlayerId) join request.
    /// </summary>
    private sealed class ThrowingDuplicatePendingJoinRequestTeamBuilderDbContext(DbContextOptions<TeamBuilderDbContext> options)
        : TeamBuilderDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var sqlException = SqlExceptionTestFactory.Create(
                2627,
                "Violation of UNIQUE KEY constraint 'UX_JoinRequests_TeamId_PlayerId_Pending'. Cannot insert duplicate key in object 'dbo.JoinRequests'.");
            throw new DbUpdateException("Simulated duplicate pending join request race", sqlException);
        }
    }

    /// <summary>
    /// Simulates an unrelated persistence failure on CreateAsync that must NOT be
    /// reinterpreted as a duplicate-pending-request conflict.
    /// </summary>
    private sealed class ThrowingUnrelatedDbUpdateExceptionOnCreateTeamBuilderDbContext(DbContextOptions<TeamBuilderDbContext> options)
        : TeamBuilderDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var sqlException = SqlExceptionTestFactory.Create(
                547,
                "The INSERT statement conflicted with the FOREIGN KEY constraint.");
            throw new DbUpdateException("Simulated unrelated persistence failure", sqlException);
        }
    }
}