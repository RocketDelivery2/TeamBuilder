using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;
using TeamBuilder.Application.Validation;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// End-to-end HTTP coverage of TB-RAPID-003: team lifecycle (LifecycleStatus) is separate from
/// recruitment (IsAcceptingMembers) and from capacity (derived from CurrentMemberCount and
/// MaxMembers), with the legacy TeamStatus kept only as a computed compatibility bridge.
/// </summary>
public sealed class TeamLifecycleRecruitmentIntegrationTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly TeamBuilderWebApplicationFactory _factory;

    public TeamLifecycleRecruitmentIntegrationTests(TeamBuilderWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<Team> SeedTeamAsync(
        Guid? ownerId = null,
        TeamLifecycleStatus lifecycleStatus = TeamLifecycleStatus.Active,
        bool isAcceptingMembers = true,
        int maxMembers = 10,
        int activeMembers = 0,
        string? category = null,
        DateTime? createdAtUtc = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = $"Team-{Guid.NewGuid():N}",
            LifecycleStatus = lifecycleStatus,
            IsAcceptingMembers = isAcceptingMembers,
            MaxMembers = maxMembers,
            CurrentMemberCount = activeMembers,
            Category = category,
            OwnerId = ownerId ?? Guid.NewGuid(),
            CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow,
            RowVersion = []
        };
        db.Teams.Add(team);

        for (var i = 0; i < activeMembers; i++)
        {
            var player = new Player { Id = Guid.NewGuid(), Username = $"m-{Guid.NewGuid():N}", CreatedAtUtc = DateTime.UtcNow, RowVersion = [] };
            db.Players.Add(player);
            db.TeamMembers.Add(new TeamMember
            {
                Id = Guid.NewGuid(),
                TeamId = team.Id,
                PlayerId = player.Id,
                Role = TeamRole.Member,
                IsActive = true,
                JoinedAtUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow,
                RowVersion = []
            });
        }

        await db.SaveChangesAsync();
        return team;
    }

    private async Task<(Guid PlayerId, string Token)> NewLinkedPlayerAsync()
    {
        var playerId = Guid.NewGuid();
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, playerId);
        return (playerId, token);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body, string token)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private async Task<(Team Team, string OwnerToken)> SeedOwnedTeamAsync(
        TeamLifecycleStatus lifecycleStatus = TeamLifecycleStatus.Active,
        bool isAcceptingMembers = true,
        int maxMembers = 10,
        int activeMembers = 0)
    {
        var (ownerId, token) = await NewLinkedPlayerAsync();
        var team = await SeedTeamAsync(ownerId, lifecycleStatus, isAcceptingMembers, maxMembers, activeMembers);
        return (team, token);
    }

    private Task<HttpResponseMessage> UpdateTeamAsync(Guid teamId, object body, string token) =>
        SendAsync(HttpMethod.Put, $"/api/v1/teams/{teamId}", body, token);

    private async Task<Team> GetStoredTeamAsync(Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        return await db.Teams.AsNoTracking().SingleAsync(t => t.Id == teamId);
    }

    private async Task<JoinRequest> SeedPendingJoinRequestAsync(Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var player = new Player { Id = Guid.NewGuid(), Username = $"applicant-{Guid.NewGuid():N}", CreatedAtUtc = DateTime.UtcNow, RowVersion = [] };
        db.Players.Add(player);
        var jr = new JoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            PlayerId = player.Id,
            Status = RequestStatus.Pending,
            RequestedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        };
        db.JoinRequests.Add(jr);
        await db.SaveChangesAsync();
        return jr;
    }

    private async Task<HttpResponseMessage> CreateJoinRequestAsync(Guid teamId)
    {
        var (_, token) = await NewLinkedPlayerAsync();
        return await SendAsync(HttpMethod.Post, "/api/v1/joinrequests", new CreateJoinRequestDto { TeamId = teamId }, token);
    }

    private static async Task<string?> ProblemDetailAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDetails>())?.Detail;

    private async Task<PaginatedResult<TeamDto>> DiscoverAsync(string category, string query)
    {
        var response = await _client.GetAsync($"/api/v1/teams?category={category}&pageSize=100&{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PaginatedResult<TeamDto>>())!;
    }

    /// <summary>
    /// One team per lifecycle/recruitment/capacity combination, isolated by category.
    /// </summary>
    private async Task<(string Category, Dictionary<string, Guid> Teams)> SeedDiscoveryMatrixAsync()
    {
        var category = $"disc-{Guid.NewGuid():N}";
        var teams = new Dictionary<string, Guid>
        {
            ["recruiting"] = (await SeedTeamAsync(category: category, isAcceptingMembers: true, maxMembers: 3, activeMembers: 1)).Id,
            ["acceptingFull"] = (await SeedTeamAsync(category: category, isAcceptingMembers: true, maxMembers: 2, activeMembers: 2)).Id,
            ["closedOpen"] = (await SeedTeamAsync(category: category, isAcceptingMembers: false, maxMembers: 3, activeMembers: 1)).Id,
            ["closedFull"] = (await SeedTeamAsync(category: category, isAcceptingMembers: false, maxMembers: 1, activeMembers: 1)).Id,
            ["inactive"] = (await SeedTeamAsync(category: category, lifecycleStatus: TeamLifecycleStatus.Inactive, isAcceptingMembers: false, maxMembers: 3)).Id,
            ["disbanded"] = (await SeedTeamAsync(category: category, lifecycleStatus: TeamLifecycleStatus.Disbanded, isAcceptingMembers: false, maxMembers: 3)).Id,
        };
        return (category, teams);
    }

    private static Guid[] Ids(Dictionary<string, Guid> teams, params string[] keys) =>
        keys.Select(k => teams[k]).ToArray();

    // ── API shape ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_ExposesLifecycleRecruitmentCapacityAndComputedLegacyStatus()
    {
        var team = await SeedTeamAsync(isAcceptingMembers: false, maxMembers: 4, activeMembers: 1);

        var response = await _client.GetAsync($"/api/v1/teams/{team.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("lifecycleStatus").GetInt32().Should().Be((int)TeamLifecycleStatus.Active);
        root.GetProperty("isAcceptingMembers").GetBoolean().Should().BeFalse();
        root.GetProperty("currentMemberCount").GetInt32().Should().Be(1);
        root.GetProperty("maxMembers").GetInt32().Should().Be(4);
        root.GetProperty("openSlots").GetInt32().Should().Be(3);
        root.GetProperty("isFull").GetBoolean().Should().BeFalse();
        root.GetProperty("hasVacancies").GetBoolean().Should().BeFalse();
        root.GetProperty("status").GetInt32().Should().Be((int)TeamStatus.Active);
    }

    // ── create / update ──────────────────────────────────────────────────────

    [Fact]
    public async Task Create_NewTeam_IsActiveAcceptingWithLegacyRecruiting()
    {
        var (_, token) = await NewLinkedPlayerAsync();

        var response = await SendAsync(HttpMethod.Post, "/api/v1/teams", new CreateTeamDto { Name = $"New-{Guid.NewGuid():N}", MaxMembers = 5 }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await response.Content.ReadFromJsonAsync<TeamDto>())!;
        dto.LifecycleStatus.Should().Be(TeamLifecycleStatus.Active);
        dto.IsAcceptingMembers.Should().BeTrue();
        dto.CurrentMemberCount.Should().Be(0);
        dto.OpenSlots.Should().Be(5);
        dto.HasVacancies.Should().BeTrue();
        dto.Status.Should().Be(TeamStatus.Recruiting);

        var stored = await GetStoredTeamAsync(dto.Id);
        stored.LifecycleStatus.Should().Be(TeamLifecycleStatus.Active);
        stored.IsAcceptingMembers.Should().BeTrue();
    }

    [Theory]
    [InlineData(TeamStatus.Recruiting, TeamLifecycleStatus.Active, true)]
    [InlineData(TeamStatus.Active, TeamLifecycleStatus.Active, false)]
    [InlineData(TeamStatus.Inactive, TeamLifecycleStatus.Inactive, false)]
    [InlineData(TeamStatus.Disbanded, TeamLifecycleStatus.Disbanded, false)]
    public async Task Update_LegacyStatus_MapsToLifecycleAndRecruitment(
        TeamStatus legacyStatus,
        TeamLifecycleStatus expectedLifecycle,
        bool expectedAccepting)
    {
        // Start from the opposite recruitment state so the mapping is observable.
        var (team, token) = await SeedOwnedTeamAsync(isAcceptingMembers: !expectedAccepting);

        var response = await UpdateTeamAsync(team.Id, new UpdateTeamDto { Status = legacyStatus }, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await response.Content.ReadFromJsonAsync<TeamDto>())!;
        dto.LifecycleStatus.Should().Be(expectedLifecycle);
        dto.IsAcceptingMembers.Should().Be(expectedAccepting);
        dto.Status.Should().Be(legacyStatus);
        var stored = await GetStoredTeamAsync(team.Id);
        stored.LifecycleStatus.Should().Be(expectedLifecycle);
        stored.IsAcceptingMembers.Should().Be(expectedAccepting);
    }

    [Fact]
    public async Task Update_LegacyFull_Returns400AndChangesNothing()
    {
        var (team, token) = await SeedOwnedTeamAsync(isAcceptingMembers: true);

        var response = await UpdateTeamAsync(team.Id, new UpdateTeamDto { Status = TeamStatus.Full, Name = "renamed" }, token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemDetailAsync(response)).Should().StartWith(TeamStateUpdateResolver.FullCannotBeSetMessage);
        var stored = await GetStoredTeamAsync(team.Id);
        stored.Name.Should().Be(team.Name);
        stored.IsAcceptingMembers.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Update_LegacyStatusCombinedWithNewFields_Returns400(bool sendLifecycle, bool sendAccepting)
    {
        var (team, token) = await SeedOwnedTeamAsync();
        var body = new UpdateTeamDto
        {
            Status = TeamStatus.Recruiting,
            LifecycleStatus = sendLifecycle ? TeamLifecycleStatus.Active : null,
            IsAcceptingMembers = sendAccepting ? true : null
        };

        var response = await UpdateTeamAsync(team.Id, body, token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemDetailAsync(response)).Should().StartWith(TeamStateUpdateResolver.AmbiguousStatusMessage);
    }

    [Theory]
    [InlineData(TeamLifecycleStatus.Inactive)]
    [InlineData(TeamLifecycleStatus.Disbanded)]
    public async Task Update_ToInactiveOrDisbanded_AutomaticallyClosesRecruitment(TeamLifecycleStatus lifecycle)
    {
        var (team, token) = await SeedOwnedTeamAsync(isAcceptingMembers: true);

        var response = await UpdateTeamAsync(team.Id, new UpdateTeamDto { LifecycleStatus = lifecycle }, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await GetStoredTeamAsync(team.Id);
        stored.LifecycleStatus.Should().Be(lifecycle);
        stored.IsAcceptingMembers.Should().BeFalse();
    }

    [Theory]
    [InlineData(TeamLifecycleStatus.Inactive)]
    [InlineData(TeamLifecycleStatus.Disbanded)]
    public async Task Update_ToInactiveOrDisbandedWhileAccepting_Returns400(TeamLifecycleStatus lifecycle)
    {
        var (team, token) = await SeedOwnedTeamAsync(isAcceptingMembers: true);

        var response = await UpdateTeamAsync(team.Id, new UpdateTeamDto { LifecycleStatus = lifecycle, IsAcceptingMembers = true }, token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemDetailAsync(response)).Should().StartWith(TeamStateUpdateResolver.NotActiveCannotAcceptMessage);
        var stored = await GetStoredTeamAsync(team.Id);
        stored.LifecycleStatus.Should().Be(TeamLifecycleStatus.Active);
        stored.IsAcceptingMembers.Should().BeTrue();
    }

    [Theory]
    [InlineData(TeamLifecycleStatus.Inactive)]
    [InlineData(TeamLifecycleStatus.Disbanded)]
    public async Task Update_OpenRecruitmentWhileStayingInactiveOrDisbanded_Returns400(TeamLifecycleStatus lifecycle)
    {
        var (team, token) = await SeedOwnedTeamAsync(lifecycle, isAcceptingMembers: false);

        var response = await UpdateTeamAsync(team.Id, new UpdateTeamDto { IsAcceptingMembers = true }, token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemDetailAsync(response)).Should().StartWith(TeamStateUpdateResolver.NotActiveCannotAcceptMessage);
        (await GetStoredTeamAsync(team.Id)).IsAcceptingMembers.Should().BeFalse();
    }

    [Theory]
    [InlineData(TeamLifecycleStatus.Inactive)]
    [InlineData(TeamLifecycleStatus.Disbanded)]
    public async Task Update_Reactivation_WithoutExplicitAcceptance_KeepsRecruitmentClosed(TeamLifecycleStatus from)
    {
        var (team, token) = await SeedOwnedTeamAsync(from, isAcceptingMembers: false);

        var response = await UpdateTeamAsync(team.Id, new UpdateTeamDto { LifecycleStatus = TeamLifecycleStatus.Active }, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await response.Content.ReadFromJsonAsync<TeamDto>())!;
        dto.LifecycleStatus.Should().Be(TeamLifecycleStatus.Active);
        dto.IsAcceptingMembers.Should().BeFalse();
        dto.Status.Should().Be(TeamStatus.Active);
    }

    [Fact]
    public async Task Update_ActivateAndOpenRecruitmentInOneRequest_Succeeds()
    {
        var (team, token) = await SeedOwnedTeamAsync(TeamLifecycleStatus.Inactive, isAcceptingMembers: false);

        var response = await UpdateTeamAsync(team.Id, new UpdateTeamDto { LifecycleStatus = TeamLifecycleStatus.Active, IsAcceptingMembers = true }, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await response.Content.ReadFromJsonAsync<TeamDto>())!;
        dto.LifecycleStatus.Should().Be(TeamLifecycleStatus.Active);
        dto.IsAcceptingMembers.Should().BeTrue();
        dto.Status.Should().Be(TeamStatus.Recruiting);
    }

    [Fact]
    public async Task Update_OmittingStateFields_LeavesLifecycleAndRecruitmentUnchanged()
    {
        var (team, token) = await SeedOwnedTeamAsync(isAcceptingMembers: false);

        var response = await UpdateTeamAsync(team.Id, new UpdateTeamDto { Description = "just a description" }, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await GetStoredTeamAsync(team.Id);
        stored.LifecycleStatus.Should().Be(TeamLifecycleStatus.Active);
        stored.IsAcceptingMembers.Should().BeFalse();
    }

    [Fact]
    public async Task Update_InvalidLifecycleStatusValue_Returns400()
    {
        var (team, token) = await SeedOwnedTeamAsync();

        var response = await UpdateTeamAsync(team.Id, new { lifecycleStatus = 99 }, token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── capacity is derived, never a status mutation ─────────────────────────

    [Fact]
    public async Task Capacity_IncreasingAndDecreasingMaxMembers_ChangesOnlyDerivedState()
    {
        var (team, token) = await SeedOwnedTeamAsync(isAcceptingMembers: true, maxMembers: 3, activeMembers: 3);
        var full = await (await _client.GetAsync($"/api/v1/teams/{team.Id}")).Content.ReadFromJsonAsync<TeamDto>();
        full!.IsFull.Should().BeTrue();
        full.Status.Should().Be(TeamStatus.Full);

        var raised = await UpdateTeamAsync(team.Id, new UpdateTeamDto { MaxMembers = 5 }, token);
        var raisedDto = (await raised.Content.ReadFromJsonAsync<TeamDto>())!;
        raisedDto.IsFull.Should().BeFalse();
        raisedDto.OpenSlots.Should().Be(2);
        raisedDto.HasVacancies.Should().BeTrue();
        raisedDto.Status.Should().Be(TeamStatus.Recruiting);

        var lowered = await UpdateTeamAsync(team.Id, new UpdateTeamDto { MaxMembers = 3 }, token);
        var loweredDto = (await lowered.Content.ReadFromJsonAsync<TeamDto>())!;
        loweredDto.IsFull.Should().BeTrue();
        loweredDto.OpenSlots.Should().Be(0);
        loweredDto.HasVacancies.Should().BeFalse();
        loweredDto.Status.Should().Be(TeamStatus.Full);

        var stored = await GetStoredTeamAsync(team.Id);
        stored.LifecycleStatus.Should().Be(TeamLifecycleStatus.Active);
        stored.IsAcceptingMembers.Should().BeTrue("capacity never changes the recruitment policy");
    }

    // ── join request creation policy ─────────────────────────────────────────

    [Theory]
    [InlineData(TeamLifecycleStatus.Inactive)]
    [InlineData(TeamLifecycleStatus.Disbanded)]
    public async Task CreateJoinRequest_ForInactiveOrDisbandedTeam_Returns409TeamNotActive(TeamLifecycleStatus lifecycle)
    {
        var team = await SeedTeamAsync(lifecycleStatus: lifecycle, isAcceptingMembers: false);

        var response = await CreateJoinRequestAsync(team.Id);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemDetailAsync(response)).Should().Be(JoinRequestService.TeamNotActiveMessage);
    }

    [Fact]
    public async Task CreateJoinRequest_ForActiveTeamWithRecruitmentClosed_Returns409NotAccepting()
    {
        var team = await SeedTeamAsync(isAcceptingMembers: false);

        var response = await CreateJoinRequestAsync(team.Id);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemDetailAsync(response)).Should().Be(JoinRequestService.TeamNotAcceptingMessage);
    }

    [Fact]
    public async Task CreateJoinRequest_ForActiveAcceptingTeamWithOpenSlot_Returns201()
    {
        var team = await SeedTeamAsync(isAcceptingMembers: true, maxMembers: 3, activeMembers: 2);

        var response = await CreateJoinRequestAsync(team.Id);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateJoinRequest_ForActiveAcceptingButPhysicallyFullTeam_StillCreatesPendingRequest()
    {
        var team = await SeedTeamAsync(isAcceptingMembers: true, maxMembers: 2, activeMembers: 2);

        var response = await CreateJoinRequestAsync(team.Id);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await response.Content.ReadFromJsonAsync<JoinRequestDto>())!;
        dto.Status.Should().Be(RequestStatus.Pending);
    }

    [Fact]
    public async Task CreateJoinRequest_ForMissingTeam_Returns404()
    {
        var response = await CreateJoinRequestAsync(Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── join request approval ────────────────────────────────────────────────

    [Theory]
    [InlineData(TeamLifecycleStatus.Inactive)]
    [InlineData(TeamLifecycleStatus.Disbanded)]
    public async Task Approve_OnInactiveOrDisbandedTeam_Returns409AndAddsNoMember(TeamLifecycleStatus lifecycle)
    {
        var (team, token) = await SeedOwnedTeamAsync(lifecycle, isAcceptingMembers: false);
        var jr = await SeedPendingJoinRequestAsync(team.Id);

        var response = await SendAsync(HttpMethod.Put, $"/api/v1/joinrequests/{jr.Id}/process", new ProcessJoinRequestDto { Status = RequestStatus.Approved }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemDetailAsync(response)).Should().Be(JoinRequestService.TeamNotActiveMessage);
        var stored = await GetStoredTeamAsync(team.Id);
        stored.CurrentMemberCount.Should().Be(0);
    }

    [Fact]
    public async Task Reject_OnInactiveTeam_IsStillAllowed()
    {
        var (team, token) = await SeedOwnedTeamAsync(TeamLifecycleStatus.Inactive, isAcceptingMembers: false);
        var jr = await SeedPendingJoinRequestAsync(team.Id);

        var response = await SendAsync(HttpMethod.Put, $"/api/v1/joinrequests/{jr.Id}/process", new ProcessJoinRequestDto { Status = RequestStatus.Rejected }, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Approve_ExistingPendingRequest_AfterRecruitmentClosed_SucceedsWhenActiveWithCapacity()
    {
        var (team, token) = await SeedOwnedTeamAsync(isAcceptingMembers: true, maxMembers: 2, activeMembers: 1);
        var jr = await SeedPendingJoinRequestAsync(team.Id);

        var close = await UpdateTeamAsync(team.Id, new UpdateTeamDto { IsAcceptingMembers = false }, token);
        close.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await SendAsync(HttpMethod.Put, $"/api/v1/joinrequests/{jr.Id}/process", new ProcessJoinRequestDto { Status = RequestStatus.Approved }, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await GetStoredTeamAsync(team.Id);
        stored.CurrentMemberCount.Should().Be(2);
        stored.IsFull.Should().BeTrue();
        stored.IsAcceptingMembers.Should().BeFalse();
        stored.LegacyStatus.Should().Be(TeamStatus.Full);
    }

    [Fact]
    public async Task Approve_ToMaxMembers_MakesTeamDerivedFull_WithoutPersistedStatusChange()
    {
        var (team, token) = await SeedOwnedTeamAsync(isAcceptingMembers: true, maxMembers: 2, activeMembers: 1);
        var jr = await SeedPendingJoinRequestAsync(team.Id);

        var response = await SendAsync(HttpMethod.Put, $"/api/v1/joinrequests/{jr.Id}/process", new ProcessJoinRequestDto { Status = RequestStatus.Approved }, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await (await _client.GetAsync($"/api/v1/teams/{team.Id}")).Content.ReadFromJsonAsync<TeamDto>())!;
        dto.IsFull.Should().BeTrue();
        dto.OpenSlots.Should().Be(0);
        dto.HasVacancies.Should().BeFalse();
        dto.Status.Should().Be(TeamStatus.Full);
        dto.LifecycleStatus.Should().Be(TeamLifecycleStatus.Active);
        dto.IsAcceptingMembers.Should().BeTrue("reaching capacity does not close recruitment");
    }

    [Fact]
    public async Task Approve_WhilePhysicallyFull_StillReturns409()
    {
        var (team, token) = await SeedOwnedTeamAsync(isAcceptingMembers: true, maxMembers: 1, activeMembers: 1);
        var jr = await SeedPendingJoinRequestAsync(team.Id);

        var response = await SendAsync(HttpMethod.Put, $"/api/v1/joinrequests/{jr.Id}/process", new ProcessJoinRequestDto { Status = RequestStatus.Approved }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Leave_CreatesOpenSlot_WithoutStatusMutation()
    {
        var (memberId, memberToken) = await NewLinkedPlayerAsync();
        var team = await SeedTeamAsync(isAcceptingMembers: false, maxMembers: 2, activeMembers: 1);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
            db.TeamMembers.Add(new TeamMember { Id = Guid.NewGuid(), TeamId = team.Id, PlayerId = memberId, Role = TeamRole.Member, IsActive = true, JoinedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow, RowVersion = [] });
            var stored = await db.Teams.SingleAsync(t => t.Id == team.Id);
            stored.CurrentMemberCount = 2;
            await db.SaveChangesAsync();
        }
        (await GetStoredTeamAsync(team.Id)).LegacyStatus.Should().Be(TeamStatus.Full);

        var response = await SendAsync(HttpMethod.Post, $"/api/v1/teams/{team.Id}/members/{memberId}/leave", null, memberToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var after = await GetStoredTeamAsync(team.Id);
        after.CurrentMemberCount.Should().Be(1);
        after.OpenSlots.Should().Be(1);
        after.IsFull.Should().BeFalse();
        after.IsAcceptingMembers.Should().BeFalse("leaving never reopens recruitment");
        after.LegacyStatus.Should().Be(TeamStatus.Active);
    }

    // ── discovery ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Discover_HasVacanciesTrue_ReturnsOnlyActiveAcceptingTeamsWithOpenSlots()
    {
        var (category, teams) = await SeedDiscoveryMatrixAsync();

        var result = await DiscoverAsync(category, "hasVacancies=true");

        result.Items.Select(t => t.Id).Should().BeEquivalentTo(Ids(teams, "recruiting"));
        result.TotalCount.Should().Be(1);
        result.Items.Should().OnlyContain(t => t.HasVacancies);
    }

    [Fact]
    public async Task Discover_HasVacanciesFalse_ReturnsTheExactLogicalInverse()
    {
        var (category, teams) = await SeedDiscoveryMatrixAsync();

        var result = await DiscoverAsync(category, "hasVacancies=false");

        result.Items.Select(t => t.Id).Should().BeEquivalentTo(
            Ids(teams, "acceptingFull", "closedOpen", "closedFull", "inactive", "disbanded"));
        result.Items.Should().OnlyContain(t => !t.HasVacancies);
    }

    [Theory]
    [InlineData(TeamLifecycleStatus.Active, new[] { "recruiting", "acceptingFull", "closedOpen", "closedFull" })]
    [InlineData(TeamLifecycleStatus.Inactive, new[] { "inactive" })]
    [InlineData(TeamLifecycleStatus.Disbanded, new[] { "disbanded" })]
    public async Task Discover_ByLifecycleStatus(TeamLifecycleStatus lifecycle, string[] expected)
    {
        var (category, teams) = await SeedDiscoveryMatrixAsync();

        var result = await DiscoverAsync(category, $"lifecycleStatus={lifecycle}");

        result.Items.Select(t => t.Id).Should().BeEquivalentTo(Ids(teams, expected));
    }

    [Theory]
    [InlineData(TeamStatus.Recruiting, new[] { "recruiting" })]
    [InlineData(TeamStatus.Full, new[] { "acceptingFull", "closedFull" })]
    [InlineData(TeamStatus.Active, new[] { "closedOpen" })]
    [InlineData(TeamStatus.Inactive, new[] { "inactive" })]
    [InlineData(TeamStatus.Disbanded, new[] { "disbanded" })]
    public async Task Discover_LegacyStatus_TranslatesToStoredFacts(TeamStatus legacy, string[] expected)
    {
        var (category, teams) = await SeedDiscoveryMatrixAsync();

        var result = await DiscoverAsync(category, $"status={legacy}");

        result.Items.Select(t => t.Id).Should().BeEquivalentTo(Ids(teams, expected));
        result.Items.Should().OnlyContain(t => t.Status == legacy, "the filter matches the computed legacy status");
    }

    [Theory]
    [InlineData("status=Full&hasVacancies=false", new[] { "acceptingFull", "closedFull" })]
    [InlineData("status=Full&hasVacancies=true", new string[0])]
    [InlineData("status=Recruiting&lifecycleStatus=Active", new[] { "recruiting" })]
    [InlineData("status=Inactive&lifecycleStatus=Active", new string[0])]
    [InlineData("status=Active&hasVacancies=false&lifecycleStatus=Active", new[] { "closedOpen" })]
    public async Task Discover_CombinedLegacyAndNewFilters_UseAndSemantics(string query, string[] expected)
    {
        var (category, teams) = await SeedDiscoveryMatrixAsync();

        var result = await DiscoverAsync(category, query);

        result.Items.Select(t => t.Id).Should().BeEquivalentTo(Ids(teams, expected));
        result.TotalCount.Should().Be(expected.Length);
    }

    [Fact]
    public async Task Discover_PaginationTotalReflectsFiltersBeforeSkipTake()
    {
        var category = $"page-{Guid.NewGuid():N}";
        var start = DateTime.UtcNow.AddDays(-1);
        for (var i = 0; i < 5; i++)
            await SeedTeamAsync(category: category, isAcceptingMembers: true, maxMembers: 3, createdAtUtc: start.AddMinutes(i));
        for (var i = 0; i < 3; i++)
            await SeedTeamAsync(category: category, isAcceptingMembers: false, maxMembers: 3, createdAtUtc: start.AddMinutes(10 + i));

        var page1 = await _client.GetFromJsonAsync<PaginatedResult<TeamDto>>($"/api/v1/teams?category={category}&hasVacancies=true&page=1&pageSize=2");
        var page3 = await _client.GetFromJsonAsync<PaginatedResult<TeamDto>>($"/api/v1/teams?category={category}&hasVacancies=true&page=3&pageSize=2");

        page1!.TotalCount.Should().Be(5);
        page1.Items.Should().HaveCount(2).And.OnlyContain(t => t.HasVacancies);
        page3!.TotalCount.Should().Be(5);
        page3.Items.Should().HaveCount(1);
    }
}
