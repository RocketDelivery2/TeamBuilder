using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// TB-RAPID-003 against a real SQL Server: capacity is derived (no status is written on joins,
/// leaves, MaxMembers changes or player deletion), the recruitment/lifecycle policies hold, the
/// capacity CHECK still guards the stored count, and every discovery filter translates to SQL
/// with the documented semantics.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class TeamLifecycleRecruitmentSqlServerIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public TeamLifecycleRecruitmentSqlServerIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "lifecycle");
        await _db.MigrateToAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ── capacity derivation ──────────────────────────────────────────────────

    [Fact]
    public async Task Approval_ToMaxMembers_IsFullDerived_LifecycleAndRecruitmentUntouched()
    {
        var (teamId, ownerId) = await SeedOwnedTeamAsync(maxMembers: 2, activeMembers: 1, accepting: true);
        var (requestId, _) = await SeedPendingRequestAsync(teamId);

        await using (var context = _db.CreateContext())
        {
            await new JoinRequestService(context).ProcessAsync(
                requestId, new ProcessJoinRequestDto { Status = RequestStatus.Approved }, ownerId);
        }

        var team = await GetTeamAsync(teamId);
        team.CurrentMemberCount.Should().Be(2);
        team.IsFull.Should().BeTrue();
        team.OpenSlots.Should().Be(0);
        team.LegacyStatus.Should().Be(TeamStatus.Full);
        team.LifecycleStatus.Should().Be(TeamLifecycleStatus.Active);
        team.IsAcceptingMembers.Should().BeTrue();
    }

    [Fact]
    public async Task Leave_CreatesOpenSlot_WithoutChangingStoredState()
    {
        var (teamId, _) = await SeedOwnedTeamAsync(maxMembers: 2, activeMembers: 1, accepting: false);
        var leaverId = await SeedPlayerAsync();
        await AddMembershipAsync(teamId, leaverId);
        await SetStoredCountAsync(teamId, 2);

        await using (var context = _db.CreateContext())
        {
            (await new TeamService(context).RemoveMemberAsync(teamId, leaverId)).Should().BeTrue();
        }

        var team = await GetTeamAsync(teamId);
        team.CurrentMemberCount.Should().Be(1);
        team.OpenSlots.Should().Be(1);
        team.IsFull.Should().BeFalse();
        team.IsAcceptingMembers.Should().BeFalse();
        team.LifecycleStatus.Should().Be(TeamLifecycleStatus.Active);
    }

    [Fact]
    public async Task MaxMembersChanges_OnlyChangeDerivedCapacity()
    {
        var (teamId, _) = await SeedOwnedTeamAsync(maxMembers: 3, activeMembers: 3, accepting: true);

        var raised = await UpdateTeamAsync(teamId, new UpdateTeamDto { MaxMembers = 6 });
        raised!.IsFull.Should().BeFalse();
        raised.OpenSlots.Should().Be(3);
        raised.HasVacancies.Should().BeTrue();

        var exact = await UpdateTeamAsync(teamId, new UpdateTeamDto { MaxMembers = 3 });
        exact!.IsFull.Should().BeTrue();
        exact.OpenSlots.Should().Be(0);
        exact.HasVacancies.Should().BeFalse();

        var team = await GetTeamAsync(teamId);
        team.LifecycleStatus.Should().Be(TeamLifecycleStatus.Active);
        team.IsAcceptingMembers.Should().BeTrue();
    }

    [Fact]
    public async Task DeletePlayer_ReconcilesCount_WithoutChangingRecruitment()
    {
        var (teamId, _) = await SeedOwnedTeamAsync(maxMembers: 2, activeMembers: 1, accepting: false);
        var playerId = await SeedPlayerAsync();
        await AddMembershipAsync(teamId, playerId);
        await SetStoredCountAsync(teamId, 2);

        await using (var context = _db.CreateContext())
        {
            (await new PlayerService(context).DeleteAsync(playerId)).Should().BeTrue();
        }

        var team = await GetTeamAsync(teamId);
        team.CurrentMemberCount.Should().Be(1);
        team.IsAcceptingMembers.Should().BeFalse();
        team.LegacyStatus.Should().Be(TeamStatus.Active);
    }

    [Fact]
    public async Task CapacityCheckConstraint_IsStillEnforced()
    {
        var (teamId, _) = await SeedOwnedTeamAsync(maxMembers: 2, activeMembers: 0, accepting: true);

        var tooHigh = () => ExecuteAsync("UPDATE [Teams] SET [CurrentMemberCount] = 3 WHERE [Id] = @id", teamId);
        var negative = () => ExecuteAsync("UPDATE [Teams] SET [CurrentMemberCount] = -1 WHERE [Id] = @id", teamId);

        var ex = (await tooHigh.Should().ThrowAsync<SqlException>()).Which;
        ex.Number.Should().Be(547);
        ex.Message.Should().Contain("CK_Teams_CurrentMemberCount_Bounds");
        (await negative.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(547);
        (await GetTeamAsync(teamId)).CurrentMemberCount.Should().Be(0);
    }

    // ── join request policy ──────────────────────────────────────────────────

    [Theory]
    [InlineData(TeamLifecycleStatus.Inactive, false, "Team is not active.")]
    [InlineData(TeamLifecycleStatus.Disbanded, false, "Team is not active.")]
    [InlineData(TeamLifecycleStatus.Active, false, "Team is not currently accepting new members.")]
    public async Task CreateJoinRequest_RejectedByPolicy(TeamLifecycleStatus lifecycle, bool accepting, string message)
    {
        var teamId = await SeedTeamAsync(maxMembers: 5, lifecycle: lifecycle, accepting: accepting);
        var playerId = await SeedPlayerAsync();

        await using var context = _db.CreateContext();
        var act = () => new JoinRequestService(context).CreateAsync(new CreateJoinRequestDto { TeamId = teamId }, playerId);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(message);
        (await context.JoinRequests.CountAsync(jr => jr.TeamId == teamId)).Should().Be(0);
    }

    [Fact]
    public async Task CreateJoinRequest_ForFullButAcceptingTeam_IsCreatedPending()
    {
        var (teamId, _) = await SeedOwnedTeamAsync(maxMembers: 1, activeMembers: 1, accepting: true);
        var playerId = await SeedPlayerAsync();

        await using var context = _db.CreateContext();
        var created = await new JoinRequestService(context).CreateAsync(new CreateJoinRequestDto { TeamId = teamId }, playerId);

        created.Status.Should().Be(RequestStatus.Pending);
    }

    [Fact]
    public async Task CreateJoinRequest_ForMissingTeam_ThrowsTeamNotFound()
    {
        var playerId = await SeedPlayerAsync();

        await using var context = _db.CreateContext();
        var act = () => new JoinRequestService(context).CreateAsync(new CreateJoinRequestDto { TeamId = Guid.NewGuid() }, playerId);

        await act.Should().ThrowAsync<TeamNotFoundException>();
    }

    [Theory]
    [InlineData(TeamLifecycleStatus.Inactive)]
    [InlineData(TeamLifecycleStatus.Disbanded)]
    public async Task Approval_OnNonActiveTeam_Conflicts(TeamLifecycleStatus lifecycle)
    {
        var (teamId, ownerId) = await SeedOwnedTeamAsync(maxMembers: 3, activeMembers: 0, accepting: false, lifecycle: lifecycle);
        var (requestId, _) = await SeedPendingRequestAsync(teamId);

        await using var context = _db.CreateContext();
        var act = () => new JoinRequestService(context).ProcessAsync(
            requestId, new ProcessJoinRequestDto { Status = RequestStatus.Approved }, ownerId);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("Team is not active.");
        (await CountActiveAsync(teamId)).Should().Be(0);
    }

    [Fact]
    public async Task Approval_AfterRecruitmentClosed_Succeeds()
    {
        var (teamId, ownerId) = await SeedOwnedTeamAsync(maxMembers: 3, activeMembers: 0, accepting: true);
        var (requestId, applicantId) = await SeedPendingRequestAsync(teamId);
        await UpdateTeamAsync(teamId, new UpdateTeamDto { IsAcceptingMembers = false });

        await using (var context = _db.CreateContext())
        {
            var result = await new JoinRequestService(context).ProcessAsync(
                requestId, new ProcessJoinRequestDto { Status = RequestStatus.Approved }, ownerId);
            result!.Status.Should().Be(RequestStatus.Approved);
        }

        (await CountActiveAsync(teamId)).Should().Be(1);
        (await GetTeamAsync(teamId)).IsAcceptingMembers.Should().BeFalse();
        applicantId.Should().NotBeEmpty();
    }

    // ── discovery translated by SQL Server ───────────────────────────────────

    private async Task<Dictionary<string, Guid>> SeedDiscoveryMatrixAsync(string category) => new()
    {
        ["recruiting"] = await SeedTeamAsync(3, storedCount: 1, accepting: true, category: category),
        ["acceptingFull"] = await SeedTeamAsync(2, storedCount: 2, accepting: true, category: category),
        ["closedOpen"] = await SeedTeamAsync(3, storedCount: 1, accepting: false, category: category),
        ["closedFull"] = await SeedTeamAsync(1, storedCount: 1, accepting: false, category: category),
        ["inactive"] = await SeedTeamAsync(3, lifecycle: TeamLifecycleStatus.Inactive, accepting: false, category: category),
        ["disbanded"] = await SeedTeamAsync(3, lifecycle: TeamLifecycleStatus.Disbanded, accepting: false, category: category),
    };

    public static TheoryData<TeamStatus?, TeamLifecycleStatus?, bool?, string[]> DiscoveryCases => new()
    {
        { null, null, true, ["recruiting"] },
        { null, null, false, ["acceptingFull", "closedOpen", "closedFull", "inactive", "disbanded"] },
        { null, TeamLifecycleStatus.Active, null, ["recruiting", "acceptingFull", "closedOpen", "closedFull"] },
        { null, TeamLifecycleStatus.Inactive, null, ["inactive"] },
        { null, TeamLifecycleStatus.Disbanded, null, ["disbanded"] },
        { TeamStatus.Recruiting, null, null, ["recruiting"] },
        { TeamStatus.Full, null, null, ["acceptingFull", "closedFull"] },
        { TeamStatus.Active, null, null, ["closedOpen"] },
        { TeamStatus.Inactive, null, null, ["inactive"] },
        { TeamStatus.Disbanded, null, null, ["disbanded"] },
        { TeamStatus.Full, null, false, ["acceptingFull", "closedFull"] },
        { TeamStatus.Full, TeamLifecycleStatus.Active, true, [] },
        { TeamStatus.Inactive, TeamLifecycleStatus.Active, null, [] },
        { TeamStatus.Active, TeamLifecycleStatus.Active, false, ["closedOpen"] },
    };

    [Theory]
    [MemberData(nameof(DiscoveryCases))]
    public async Task Discovery_FiltersTranslateToSql(
        TeamStatus? status,
        TeamLifecycleStatus? lifecycleStatus,
        bool? hasVacancies,
        string[] expected)
    {
        var category = $"disc_{Guid.NewGuid():N}";
        var teams = await SeedDiscoveryMatrixAsync(category);

        await using var context = _db.CreateContext();
        var result = await new TeamService(context).GetAllAsync(
            1, 100, category: category, status: status, lifecycleStatus: lifecycleStatus, hasVacancies: hasVacancies);

        result.Items.Select(t => t.Id).Should().BeEquivalentTo(expected.Select(k => teams[k]));
        result.TotalCount.Should().Be(expected.Length);
    }

    [Fact]
    public async Task Discovery_TotalCountIsComputedBeforePaging()
    {
        var category = $"page_{Guid.NewGuid():N}";
        for (var i = 0; i < 5; i++)
            await SeedTeamAsync(3, accepting: true, category: category);
        for (var i = 0; i < 2; i++)
            await SeedTeamAsync(3, accepting: false, category: category);

        await using var context = _db.CreateContext();
        var page = await new TeamService(context).GetAllAsync(2, 2, category: category, hasVacancies: true);

        page.TotalCount.Should().Be(5);
        page.Items.Should().HaveCount(2).And.OnlyContain(t => t.HasVacancies);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

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
        int storedCount = 0,
        TeamLifecycleStatus lifecycle = TeamLifecycleStatus.Active,
        bool accepting = true,
        Guid? ownerId = null,
        string? category = null)
    {
        await using var context = _db.CreateContext();
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = $"team_{Guid.NewGuid():N}",
            MaxMembers = maxMembers,
            CurrentMemberCount = storedCount,
            LifecycleStatus = lifecycle,
            IsAcceptingMembers = accepting,
            OwnerId = ownerId,
            Category = category
        };
        context.Teams.Add(team);
        await context.SaveChangesAsync();
        return team.Id;
    }

    private async Task<(Guid TeamId, Guid OwnerId)> SeedOwnedTeamAsync(
        int maxMembers,
        int activeMembers,
        bool accepting,
        TeamLifecycleStatus lifecycle = TeamLifecycleStatus.Active)
    {
        var ownerId = await SeedPlayerAsync();
        var teamId = await SeedTeamAsync(maxMembers, activeMembers, lifecycle, accepting, ownerId);
        for (var i = 0; i < activeMembers; i++)
            await AddMembershipAsync(teamId, await SeedPlayerAsync());
        return (teamId, ownerId);
    }

    private async Task AddMembershipAsync(Guid teamId, Guid playerId)
    {
        await using var context = _db.CreateContext();
        context.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            PlayerId = playerId,
            Role = TeamRole.Member,
            JoinedAtUtc = DateTime.UtcNow,
            IsActive = true
        });
        await context.SaveChangesAsync();
    }

    private Task SetStoredCountAsync(Guid teamId, int count) =>
        ExecuteAsync($"UPDATE [Teams] SET [CurrentMemberCount] = {count} WHERE [Id] = @id", teamId);

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

    private async Task ExecuteAsync(string sql, Guid id)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync();
    }
}
