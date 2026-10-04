using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves team capacity accounting against a real SQL Server: active TeamMember rows are the
/// roster occupancy authority, Team.CurrentMemberCount is reconciled from them on every
/// capacity-dependent write, Team.RowVersion turns concurrent capacity writes into a
/// deterministic conflict, and player deletion reconciles affected teams atomically.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class TeamCapacityAccountingSqlServerIntegrationTests : IAsyncLifetime
{
    private const string FullMessage = "The team is already full.";

    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public TeamCapacityAccountingSqlServerIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "capacity");
        await _db.MigrateToAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ── Team creation ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTeam_SetsCountZero_SetsCreatorAsOwner_AndCreatesNoOwnerMembership()
    {
        var ownerId = await SeedPlayerAsync();

        TeamDto created;
        await using (var context = _db.CreateContext())
        {
            created = await new TeamService(context).CreateAsync(
                new CreateTeamDto { Name = $"team_{Guid.NewGuid():N}", MaxMembers = 5 },
                ownerId);
        }

        created.CurrentMemberCount.Should().Be(0);
        created.OwnerId.Should().Be(ownerId);

        var team = await GetTeamAsync(created.Id);
        team.CurrentMemberCount.Should().Be(0);
        team.OwnerId.Should().Be(ownerId);

        await using var verify = _db.CreateContext();
        (await verify.TeamMembers.AnyAsync(tm => tm.TeamId == created.Id))
            .Should().BeFalse("the owner is administrative authority only and does not consume a roster slot");
    }

    // ── Team update / MaxMembers ─────────────────────────────────────────────

    [Fact]
    public async Task UpdateTeam_ReconcilesStaleStoredCount_FromActiveMemberships()
    {
        var teamId = await SeedTeamAsync(maxMembers: 10, storedCount: 7);
        await AddActiveMembersAsync(teamId, 3);
        await AddMembershipAsync(teamId, await SeedPlayerAsync(), isActive: false);

        var result = await UpdateTeamAsync(teamId, new UpdateTeamDto { Name = $"renamed_{Guid.NewGuid():N}" });

        result!.CurrentMemberCount.Should().Be(3);
        (await GetTeamAsync(teamId)).CurrentMemberCount.Should().Be(3);
    }

    [Fact]
    public async Task UpdateTeam_StaleLowStoredCount_DoesNotAllowMaxMembersBelowActiveMemberships()
    {
        // Stored count claims 1 member, but there are really 4 active memberships.
        var teamId = await SeedTeamAsync(maxMembers: 10, storedCount: 1);
        await AddActiveMembersAsync(teamId, 4);

        var act = () => UpdateTeamAsync(teamId, new UpdateTeamDto { MaxMembers = 3 });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot be lower than the team's current active member count (4)*");

        var team = await GetTeamAsync(teamId);
        team.MaxMembers.Should().Be(10);
        team.CurrentMemberCount.Should().Be(1, "a rejected update saves nothing");
    }

    [Fact]
    public async Task UpdateTeam_LoweringMaxMembersToExactlyActiveCount_Succeeds()
    {
        var teamId = await SeedTeamAsync(maxMembers: 10, storedCount: 4, status: TeamStatus.Active);
        await AddActiveMembersAsync(teamId, 4);

        var result = await UpdateTeamAsync(teamId, new UpdateTeamDto { MaxMembers = 4 });

        result!.MaxMembers.Should().Be(4);
        result.CurrentMemberCount.Should().Be(4);
        result.Status.Should().Be(TeamStatus.Active, "only Full/Recruiting transition on capacity changes");
    }

    [Fact]
    public async Task UpdateTeam_IncreasingFullTeamCapacity_TransitionsToRecruiting()
    {
        var teamId = await SeedTeamAsync(maxMembers: 3, storedCount: 3, status: TeamStatus.Full);
        await AddActiveMembersAsync(teamId, 3);

        var result = await UpdateTeamAsync(teamId, new UpdateTeamDto { MaxMembers = 5 });

        result!.Status.Should().Be(TeamStatus.Recruiting);
        (await GetTeamAsync(teamId)).Status.Should().Be(TeamStatus.Recruiting);
    }

    [Fact]
    public async Task UpdateTeam_DecreasingRecruitingTeamCapacityToActiveCount_TransitionsToFull()
    {
        // Stale-high stored count (6) must not matter: the 3 active memberships decide.
        var teamId = await SeedTeamAsync(maxMembers: 8, storedCount: 6, status: TeamStatus.Recruiting);
        await AddActiveMembersAsync(teamId, 3);

        var result = await UpdateTeamAsync(teamId, new UpdateTeamDto { MaxMembers = 3 });

        result!.Status.Should().Be(TeamStatus.Full);
        result.CurrentMemberCount.Should().Be(3);
    }

    // ── Join approval ────────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_StaleLowStoredCount_CannotOverfillTeam()
    {
        // Stored count says 1 of 3, but 3 active memberships already exist.
        var (teamId, ownerId) = await SeedOwnedTeamAsync(maxMembers: 3, storedCount: 1);
        await AddActiveMembersAsync(teamId, 3);
        var (requestId, applicantId) = await SeedPendingRequestAsync(teamId);

        var act = () => ApproveAsync(requestId, ownerId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(FullMessage);
        (await CountActiveAsync(teamId)).Should().Be(3);
        (await IsActiveMemberAsync(teamId, applicantId)).Should().BeFalse();
        (await GetRequestStatusAsync(requestId)).Should().Be(RequestStatus.Pending);
    }

    [Fact]
    public async Task Approve_StaleHighStoredCount_DoesNotBlockJoinWhenActualMembershipHasSpace()
    {
        // Stored count claims the team is full, but only 1 of 3 slots is really occupied.
        var (teamId, ownerId) = await SeedOwnedTeamAsync(maxMembers: 3, storedCount: 3, status: TeamStatus.Recruiting);
        await AddActiveMembersAsync(teamId, 1);
        var (requestId, applicantId) = await SeedPendingRequestAsync(teamId);

        var result = await ApproveAsync(requestId, ownerId);

        result!.Status.Should().Be(RequestStatus.Approved);
        (await IsActiveMemberAsync(teamId, applicantId)).Should().BeTrue();
        var team = await GetTeamAsync(teamId);
        team.CurrentMemberCount.Should().Be(2);
        team.Status.Should().Be(TeamStatus.Recruiting);
    }

    [Fact]
    public async Task Approve_SetsExactCount_ToPreviousActiveCountPlusOne_IgnoringInactiveHistory()
    {
        var (teamId, ownerId) = await SeedOwnedTeamAsync(maxMembers: 10, storedCount: 9);
        await AddActiveMembersAsync(teamId, 4);
        await AddMembershipAsync(teamId, await SeedPlayerAsync(), isActive: false);
        await AddMembershipAsync(teamId, await SeedPlayerAsync(), isActive: false);
        var (requestId, _) = await SeedPendingRequestAsync(teamId);

        await ApproveAsync(requestId, ownerId);

        (await GetTeamAsync(teamId)).CurrentMemberCount.Should().Be(5);
        (await CountActiveAsync(teamId)).Should().Be(5);
    }

    [Fact]
    public async Task Approve_FinalSlot_SetsTeamFull()
    {
        var (teamId, ownerId) = await SeedOwnedTeamAsync(maxMembers: 3, storedCount: 0, status: TeamStatus.Recruiting);
        await AddActiveMembersAsync(teamId, 2);
        var (requestId, _) = await SeedPendingRequestAsync(teamId);

        await ApproveAsync(requestId, ownerId);

        var team = await GetTeamAsync(teamId);
        team.CurrentMemberCount.Should().Be(3);
        team.Status.Should().Be(TeamStatus.Full);
    }

    [Fact]
    public async Task Approve_ApplicantAlreadyActiveMember_StillRejectedAsDuplicate()
    {
        var (teamId, ownerId) = await SeedOwnedTeamAsync(maxMembers: 10, storedCount: 1);
        var (requestId, applicantId) = await SeedPendingRequestAsync(teamId);
        await AddMembershipAsync(teamId, applicantId, isActive: true);

        var act = () => ApproveAsync(requestId, ownerId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("This player is already an active member of this team.");
        (await CountActiveAsync(teamId)).Should().Be(1);
        (await GetRequestStatusAsync(requestId)).Should().Be(RequestStatus.Pending);
    }

    [Fact]
    public async Task Approve_ConcurrentApprovalForLastSlot_SecondSaveConflictsDeterministically()
    {
        var (teamId, ownerId) = await SeedOwnedTeamAsync(maxMembers: 2, storedCount: 1, status: TeamStatus.Recruiting);
        await AddActiveMembersAsync(teamId, 1);
        var (firstRequestId, firstApplicantId) = await SeedPendingRequestAsync(teamId);
        var (secondRequestId, secondApplicantId) = await SeedPendingRequestAsync(teamId);

        // The second approval commits after the first has read the team and counted members,
        // but before the first saves.
        await using var context = CreateInterferingContext(async () =>
        {
            await using var other = _db.CreateContext();
            await new JoinRequestService(other).ProcessAsync(
                secondRequestId, new ProcessJoinRequestDto { Status = RequestStatus.Approved }, ownerId);
        });

        var act = () => new JoinRequestService(context).ProcessAsync(
            firstRequestId, new ProcessJoinRequestDto { Status = RequestStatus.Approved }, ownerId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The team changed while this join request was being processed. Please try again.");

        (await CountActiveAsync(teamId)).Should().Be(2, "the team must never be overfilled");
        (await IsActiveMemberAsync(teamId, secondApplicantId)).Should().BeTrue();
        (await IsActiveMemberAsync(teamId, firstApplicantId)).Should().BeFalse();
        (await GetRequestStatusAsync(firstRequestId)).Should().Be(RequestStatus.Pending);
        var team = await GetTeamAsync(teamId);
        team.CurrentMemberCount.Should().Be(2);
        team.Status.Should().Be(TeamStatus.Full);
    }

    [Fact]
    public async Task Approve_ConcurrentApproval_ConflictsEvenWhenStaleStoredCountAlreadyMatchesNewValue()
    {
        // Stored count is stale-high by one, so the first approval's computed count (1 + 1 = 2)
        // equals the stored value. The team UPDATE must still be issued (and RowVersion-checked)
        // or two concurrent approvals could both commit.
        var (teamId, ownerId) = await SeedOwnedTeamAsync(maxMembers: 2, storedCount: 2, status: TeamStatus.Recruiting);
        await AddActiveMembersAsync(teamId, 1);
        var (firstRequestId, _) = await SeedPendingRequestAsync(teamId);
        var (secondRequestId, _) = await SeedPendingRequestAsync(teamId);

        await using var context = CreateInterferingContext(async () =>
        {
            await using var other = _db.CreateContext();
            await new JoinRequestService(other).ProcessAsync(
                secondRequestId, new ProcessJoinRequestDto { Status = RequestStatus.Approved }, ownerId);
        });

        var act = () => new JoinRequestService(context).ProcessAsync(
            firstRequestId, new ProcessJoinRequestDto { Status = RequestStatus.Approved }, ownerId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The team changed while this join request was being processed. Please try again.");
        (await CountActiveAsync(teamId)).Should().Be(2);
    }

    // ── Member leave ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Leave_RepairsStaleStoredCount_ToExactRemainingActiveCount()
    {
        var teamId = await SeedTeamAsync(maxMembers: 10, storedCount: 9);
        var leaverId = await SeedPlayerAsync();
        await AddMembershipAsync(teamId, leaverId, isActive: true);
        await AddActiveMembersAsync(teamId, 2);

        (await LeaveAsync(teamId, leaverId)).Should().BeTrue();

        (await GetTeamAsync(teamId)).CurrentMemberCount.Should().Be(2);
        (await IsActiveMemberAsync(teamId, leaverId)).Should().BeFalse();
    }

    [Fact]
    public async Task Leave_LastMemberWithZeroStoredCount_NeverGoesNegative()
    {
        var teamId = await SeedTeamAsync(maxMembers: 5, storedCount: 0);
        var leaverId = await SeedPlayerAsync();
        await AddMembershipAsync(teamId, leaverId, isActive: true);

        (await LeaveAsync(teamId, leaverId)).Should().BeTrue();

        (await GetTeamAsync(teamId)).CurrentMemberCount.Should().Be(0);
    }

    [Fact]
    public async Task Leave_FromFullTeam_TransitionsToRecruiting()
    {
        var teamId = await SeedTeamAsync(maxMembers: 3, storedCount: 3, status: TeamStatus.Full);
        var leaverId = await SeedPlayerAsync();
        await AddMembershipAsync(teamId, leaverId, isActive: true);
        await AddActiveMembersAsync(teamId, 2);

        await LeaveAsync(teamId, leaverId);

        var team = await GetTeamAsync(teamId);
        team.CurrentMemberCount.Should().Be(2);
        team.Status.Should().Be(TeamStatus.Recruiting);
    }

    [Fact]
    public async Task Leave_DoesNotCountInactiveHistoricalMemberships()
    {
        var teamId = await SeedTeamAsync(maxMembers: 10, storedCount: 5);
        var leaverId = await SeedPlayerAsync();
        await AddMembershipAsync(teamId, leaverId, isActive: true);
        await AddActiveMembersAsync(teamId, 1);
        await AddMembershipAsync(teamId, await SeedPlayerAsync(), isActive: false);
        await AddMembershipAsync(teamId, await SeedPlayerAsync(), isActive: false);

        await LeaveAsync(teamId, leaverId);

        (await GetTeamAsync(teamId)).CurrentMemberCount.Should().Be(1);
    }

    [Fact]
    public async Task Leave_ConcurrentLeave_ConflictsDeterministically_AndLeavesExactCount()
    {
        var teamId = await SeedTeamAsync(maxMembers: 3, storedCount: 3, status: TeamStatus.Full);
        var firstLeaverId = await SeedPlayerAsync();
        var secondLeaverId = await SeedPlayerAsync();
        await AddMembershipAsync(teamId, firstLeaverId, isActive: true);
        await AddMembershipAsync(teamId, secondLeaverId, isActive: true);
        await AddActiveMembersAsync(teamId, 1);

        await using var context = CreateInterferingContext(async () =>
        {
            await using var other = _db.CreateContext();
            await new TeamService(other).RemoveMemberAsync(teamId, secondLeaverId);
        });

        var act = () => new TeamService(context).RemoveMemberAsync(teamId, firstLeaverId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The team changed while this member was leaving. Please try again.");

        (await IsActiveMemberAsync(teamId, firstLeaverId)).Should().BeTrue();
        (await IsActiveMemberAsync(teamId, secondLeaverId)).Should().BeFalse();
        var team = await GetTeamAsync(teamId);
        team.CurrentMemberCount.Should().Be(2);
        team.Status.Should().Be(TeamStatus.Recruiting);
    }

    // ── Player deletion ──────────────────────────────────────────────────────

    [Fact]
    public async Task DeletePlayer_MemberOfMultipleTeams_ReconcilesEveryAffectedTeam()
    {
        var playerId = await SeedPlayerAsync();

        // Team A: stale-high stored count; 3 active including the player.
        var teamA = await SeedTeamAsync(maxMembers: 10, storedCount: 8, status: TeamStatus.Recruiting);
        await AddMembershipAsync(teamA, playerId, isActive: true);
        await AddActiveMembersAsync(teamA, 2);

        // Team B: stale-low stored count; 4 active including the player.
        var teamB = await SeedTeamAsync(maxMembers: 10, storedCount: 1, status: TeamStatus.Active);
        await AddMembershipAsync(teamB, playerId, isActive: true);
        await AddActiveMembersAsync(teamB, 3);

        (await DeletePlayerAsync(playerId)).Should().BeTrue();

        (await GetTeamAsync(teamA)).CurrentMemberCount.Should().Be(2);
        var b = await GetTeamAsync(teamB);
        b.CurrentMemberCount.Should().Be(3);
        b.Status.Should().Be(TeamStatus.Active);
        (await PlayerExistsAsync(playerId)).Should().BeFalse();
    }

    [Fact]
    public async Task DeletePlayer_OnFormerlyFullTeams_CreatesVacancies()
    {
        var playerId = await SeedPlayerAsync();
        var teamA = await SeedTeamAsync(maxMembers: 2, storedCount: 2, status: TeamStatus.Full);
        await AddMembershipAsync(teamA, playerId, isActive: true);
        await AddActiveMembersAsync(teamA, 1);
        var teamB = await SeedTeamAsync(maxMembers: 1, storedCount: 1, status: TeamStatus.Full);
        await AddMembershipAsync(teamB, playerId, isActive: true);

        await DeletePlayerAsync(playerId);

        var a = await GetTeamAsync(teamA);
        a.CurrentMemberCount.Should().Be(1);
        a.Status.Should().Be(TeamStatus.Recruiting);
        var b = await GetTeamAsync(teamB);
        b.CurrentMemberCount.Should().Be(0);
        b.Status.Should().Be(TeamStatus.Recruiting);
    }

    [Fact]
    public async Task DeletePlayer_LeavesUnrelatedTeamsAndInactiveHistoryOutOfReconciliation()
    {
        var playerId = await SeedPlayerAsync();

        var memberTeam = await SeedTeamAsync(maxMembers: 5, storedCount: 2);
        await AddMembershipAsync(memberTeam, playerId, isActive: true);
        await AddActiveMembersAsync(memberTeam, 1);

        // The player only has inactive history here: not an affected team, stale value untouched.
        var historyTeam = await SeedTeamAsync(maxMembers: 5, storedCount: 4, status: TeamStatus.Full);
        await AddMembershipAsync(historyTeam, playerId, isActive: false);
        await AddActiveMembersAsync(historyTeam, 1);

        // Completely unrelated team with a deliberately stale stored count.
        var unrelatedTeam = await SeedTeamAsync(maxMembers: 5, storedCount: 4, status: TeamStatus.Full);
        await AddActiveMembersAsync(unrelatedTeam, 2);

        await DeletePlayerAsync(playerId);

        (await GetTeamAsync(memberTeam)).CurrentMemberCount.Should().Be(1);

        var history = await GetTeamAsync(historyTeam);
        history.CurrentMemberCount.Should().Be(4);
        history.Status.Should().Be(TeamStatus.Full);

        var unrelated = await GetTeamAsync(unrelatedTeam);
        unrelated.CurrentMemberCount.Should().Be(4);
        unrelated.Status.Should().Be(TeamStatus.Full);
    }

    [Fact]
    public async Task DeletePlayer_WhoOwnsATeam_ConflictsAndLeavesPlayerTeamsAndMembershipsUnchanged()
    {
        var (ownedTeamId, ownerId) = await SeedOwnedTeamAsync(maxMembers: 5, storedCount: 0);

        // The owner is also a member of another team whose stored count is stale.
        var otherTeam = await SeedTeamAsync(maxMembers: 2, storedCount: 2, status: TeamStatus.Full);
        await AddMembershipAsync(otherTeam, ownerId, isActive: true);
        await AddActiveMembersAsync(otherTeam, 1);

        var act = () => DeletePlayerAsync(ownerId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Delete or transfer ownership of all owned teams before deleting this player.");

        (await PlayerExistsAsync(ownerId)).Should().BeTrue();
        var owned = await GetTeamAsync(ownedTeamId);
        owned.OwnerId.Should().Be(ownerId);
        var other = await GetTeamAsync(otherTeam);
        other.CurrentMemberCount.Should().Be(2);
        other.Status.Should().Be(TeamStatus.Full);
        (await IsActiveMemberAsync(otherTeam, ownerId)).Should().BeTrue();
    }

    [Fact]
    public async Task DeletePlayer_WhenTeamReconciliationFails_RollsBackPlayerDeleteAndAllTeamCorrections()
    {
        var playerId = await SeedPlayerAsync();

        var teamA = await SeedTeamAsync(maxMembers: 3, storedCount: 3, status: TeamStatus.Full);
        await AddMembershipAsync(teamA, playerId, isActive: true);
        await AddActiveMembersAsync(teamA, 2);

        var teamB = await SeedTeamAsync(maxMembers: 10, storedCount: 7);
        await AddMembershipAsync(teamB, playerId, isActive: true);
        var teamBOtherMember = await SeedPlayerAsync();
        await AddMembershipAsync(teamB, teamBOtherMember, isActive: true);

        // Another member leaves team B after the delete has loaded it, so B's RowVersion is stale
        // and the single SaveChanges transaction must fail and roll back as a whole.
        await using var context = CreateInterferingContext(async () =>
        {
            await using var other = _db.CreateContext();
            await new TeamService(other).RemoveMemberAsync(teamB, teamBOtherMember);
        });

        var act = () => new PlayerService(context).DeleteAsync(playerId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A team this player belongs to changed while the player was being deleted. Please try again.");

        (await PlayerExistsAsync(playerId)).Should().BeTrue();
        (await IsActiveMemberAsync(teamA, playerId)).Should().BeTrue();
        (await IsActiveMemberAsync(teamB, playerId)).Should().BeTrue();

        var a = await GetTeamAsync(teamA);
        a.CurrentMemberCount.Should().Be(3, "team A's correction must roll back with the failed delete");
        a.Status.Should().Be(TeamStatus.Full);

        // Only the concurrent leave's own reconciliation is visible on team B.
        (await GetTeamAsync(teamB)).CurrentMemberCount.Should().Be(1);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private TeamBuilderDbContext CreateInterferingContext(Func<Task> beforeFirstSave)
    {
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseSqlServer(_db.ConnectionString)
            .Options;

        return new InterferingTeamBuilderDbContext(options, beforeFirstSave);
    }

    private async Task<Guid> SeedPlayerAsync()
    {
        await using var context = _db.CreateContext();
        var player = new Player { Id = Guid.NewGuid(), Username = $"player_{Guid.NewGuid():N}" };
        context.Players.Add(player);
        await context.SaveChangesAsync();
        return player.Id;
    }

    private async Task<Guid> SeedTeamAsync(
        int maxMembers,
        int storedCount,
        TeamStatus status = TeamStatus.Recruiting,
        Guid? ownerId = null)
    {
        await using var context = _db.CreateContext();
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = $"team_{Guid.NewGuid():N}",
            MaxMembers = maxMembers,
            CurrentMemberCount = storedCount,
            Status = status,
            OwnerId = ownerId
        };
        context.Teams.Add(team);
        await context.SaveChangesAsync();
        return team.Id;
    }

    private async Task<(Guid TeamId, Guid OwnerId)> SeedOwnedTeamAsync(
        int maxMembers,
        int storedCount,
        TeamStatus status = TeamStatus.Recruiting)
    {
        var ownerId = await SeedPlayerAsync();
        var teamId = await SeedTeamAsync(maxMembers, storedCount, status, ownerId);
        return (teamId, ownerId);
    }

    private async Task AddMembershipAsync(Guid teamId, Guid playerId, bool isActive)
    {
        await using var context = _db.CreateContext();
        context.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            PlayerId = playerId,
            Role = TeamRole.Member,
            JoinedAtUtc = DateTime.UtcNow,
            IsActive = isActive
        });
        await context.SaveChangesAsync();
    }

    private async Task AddActiveMembersAsync(Guid teamId, int count)
    {
        for (var i = 0; i < count; i++)
            await AddMembershipAsync(teamId, await SeedPlayerAsync(), isActive: true);
    }

    private async Task<(Guid RequestId, Guid ApplicantId)> SeedPendingRequestAsync(Guid teamId)
    {
        var applicantId = await SeedPlayerAsync();
        await using var context = _db.CreateContext();
        var request = new JoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            PlayerId = applicantId,
            Status = RequestStatus.Pending,
            RequestedAtUtc = DateTime.UtcNow
        };
        context.JoinRequests.Add(request);
        await context.SaveChangesAsync();
        return (request.Id, applicantId);
    }

    private async Task<TeamDto?> UpdateTeamAsync(Guid teamId, UpdateTeamDto dto)
    {
        await using var context = _db.CreateContext();
        return await new TeamService(context).UpdateAsync(teamId, dto);
    }

    private async Task<JoinRequestDto?> ApproveAsync(Guid requestId, Guid ownerId)
    {
        await using var context = _db.CreateContext();
        return await new JoinRequestService(context).ProcessAsync(
            requestId, new ProcessJoinRequestDto { Status = RequestStatus.Approved }, ownerId);
    }

    private async Task<bool> LeaveAsync(Guid teamId, Guid playerId)
    {
        await using var context = _db.CreateContext();
        return await new TeamService(context).RemoveMemberAsync(teamId, playerId);
    }

    private async Task<bool> DeletePlayerAsync(Guid playerId)
    {
        await using var context = _db.CreateContext();
        return await new PlayerService(context).DeleteAsync(playerId);
    }

    private async Task<Team> GetTeamAsync(Guid teamId)
    {
        await using var context = _db.CreateContext();
        return await context.Teams.AsNoTracking().SingleAsync(t => t.Id == teamId);
    }

    private async Task<int> CountActiveAsync(Guid teamId)
    {
        await using var context = _db.CreateContext();
        return await context.TeamMembers.CountAsync(tm => tm.TeamId == teamId && tm.IsActive);
    }

    private async Task<bool> IsActiveMemberAsync(Guid teamId, Guid playerId)
    {
        await using var context = _db.CreateContext();
        return await context.TeamMembers.AnyAsync(tm => tm.TeamId == teamId && tm.PlayerId == playerId && tm.IsActive);
    }

    private async Task<bool> PlayerExistsAsync(Guid playerId)
    {
        await using var context = _db.CreateContext();
        return await context.Players.AnyAsync(p => p.Id == playerId);
    }

    private async Task<RequestStatus> GetRequestStatusAsync(Guid requestId)
    {
        await using var context = _db.CreateContext();
        return await context.JoinRequests.Where(jr => jr.Id == requestId).Select(jr => jr.Status).SingleAsync();
    }

    /// <summary>
    /// Runs a real, independently committed write (through its own context) immediately before
    /// this context's first SaveChanges, so the service under test has already read the team and
    /// counted memberships when the concurrent change lands.
    /// </summary>
    private sealed class InterferingTeamBuilderDbContext(
        DbContextOptions<TeamBuilderDbContext> options,
        Func<Task> beforeFirstSave) : TeamBuilderDbContext(options)
    {
        private Func<Task>? _beforeFirstSave = beforeFirstSave;

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var interference = _beforeFirstSave;
            _beforeFirstSave = null;
            if (interference is not null)
                await interference();

            return await base.SaveChangesAsync(cancellationToken);
        }
    }
}
