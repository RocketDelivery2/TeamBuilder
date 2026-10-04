using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Tests.Integration;

public sealed class JoinRequestsControllerIntegrationTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly TeamBuilderWebApplicationFactory _factory;

    public JoinRequestsControllerIntegrationTests(TeamBuilderWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<(Team team, Player player)> SeedTeamAndPlayerAsync(
        int maxMembers = 10,
        int currentMemberCount = 0,
        TeamStatus status = TeamStatus.Active,
        Guid? ownerId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = $"Team-{Guid.NewGuid():N}",
            Status = status,
            MaxMembers = maxMembers,
            CurrentMemberCount = currentMemberCount,
            OwnerId = ownerId ?? Guid.NewGuid(),
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        };

        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = $"player-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        };

        db.Teams.Add(team);
        db.Players.Add(player);

        // Active TeamMember rows are the roster occupancy authority, so back the seeded count
        // with that many real active memberships.
        for (var i = 0; i < currentMemberCount; i++)
        {
            var filler = new Player
            {
                Id = Guid.NewGuid(),
                Username = $"filler-{Guid.NewGuid():N}",
                CreatedAtUtc = DateTime.UtcNow,
                RowVersion = []
            };
            db.Players.Add(filler);
            db.TeamMembers.Add(new TeamMember
            {
                Id = Guid.NewGuid(),
                TeamId = team.Id,
                PlayerId = filler.Id,
                Role = TeamRole.Member,
                IsActive = true,
                JoinedAtUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow,
                RowVersion = []
            });
        }

        await db.SaveChangesAsync();
        return (team, player);
    }

    private async Task<JoinRequest> SeedPendingJoinRequestAsync(Guid teamId, Guid playerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

        var jr = new JoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            PlayerId = playerId,
            Status = RequestStatus.Pending,
            RequestedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        };

        db.JoinRequests.Add(jr);
        await db.SaveChangesAsync();
        return jr;
    }

    private async Task<int> GetJoinRequestCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        return await db.JoinRequests.CountAsync();
    }

    private async Task<(JoinRequest joinRequest, Team team, int teamMemberCount)> GetProcessingStateAsync(Guid joinRequestId, Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var joinRequest = await db.JoinRequests.AsNoTracking().SingleAsync(jr => jr.Id == joinRequestId);
        var team = await db.Teams.AsNoTracking().SingleAsync(t => t.Id == teamId);
        var teamMemberCount = await db.TeamMembers.CountAsync(tm => tm.TeamId == teamId);
        return (joinRequest, team, teamMemberCount);
    }

    private async Task<HttpResponseMessage> SendProcessAsync(Guid joinRequestId, RequestStatus status, Guid callerId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/joinrequests/{joinRequestId}/process");
        request.Content = JsonContent.Create(new ProcessJoinRequestDto { Status = status });
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, callerId));
        return await _client.SendAsync(request);
    }

    private async Task<JoinRequest> SeedJoinRequestAsync(
        Guid teamId,
        Guid playerId,
        RequestStatus status = RequestStatus.Pending,
        string? message = null,
        DateTime? requestedAtUtc = null,
        Guid? processedByUserId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

        if (!await db.Players.AnyAsync(p => p.Id == playerId))
        {
            db.Players.Add(new Player
            {
                Id = playerId,
                Username = $"player-{Guid.NewGuid():N}",
                CreatedAtUtc = DateTime.UtcNow,
                RowVersion = []
            });
        }

        var jr = new JoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            PlayerId = playerId,
            Status = status,
            Message = message,
            RequestedAtUtc = requestedAtUtc ?? DateTime.UtcNow,
            ProcessedAtUtc = status == RequestStatus.Pending ? null : DateTime.UtcNow,
            ProcessedByUserId = processedByUserId,
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        };

        db.JoinRequests.Add(jr);
        await db.SaveChangesAsync();
        return jr;
    }

    private async Task AddTeamMemberAsync(Guid teamId, Guid playerId, TeamRole role = TeamRole.Member)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        if (!await db.Players.AnyAsync(p => p.Id == playerId))
        {
            db.Players.Add(new Player
            {
                Id = playerId,
                Username = $"member-{Guid.NewGuid():N}",
                CreatedAtUtc = DateTime.UtcNow,
                RowVersion = []
            });
        }

        db.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            PlayerId = playerId,
            Role = role,
            JoinedAtUtc = DateTime.UtcNow,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        });
        await db.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> GetAsync(string url, string? token, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client ?? _client).SendAsync(request);
    }

    private async Task<HttpResponseMessage> GetAsPlayerAsync(string url, Guid callerId)
        => await GetAsync(url, await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, callerId));

    private static string ById(Guid id) => $"/api/v1/joinrequests/{id}";
    private static string ByTeam(Guid teamId, string query = "") => $"/api/v1/joinrequests/teams/{teamId}{query}";
    private static string ByPlayer(Guid playerId, string query = "") => $"/api/v1/joinrequests/players/{playerId}{query}";

    // ── GET reads: authentication ────────────────────────────────────────────

    public enum ReadEndpoint { ById, ByTeam, ByPlayer }

    private async Task<string> SeedUrlForAsync(ReadEndpoint endpoint)
    {
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);
        return endpoint switch
        {
            ReadEndpoint.ById => ById(jr.Id),
            ReadEndpoint.ByTeam => ByTeam(team.Id),
            _ => ByPlayer(player.Id)
        };
    }

    [Theory]
    [InlineData(ReadEndpoint.ById)]
    [InlineData(ReadEndpoint.ByTeam)]
    [InlineData(ReadEndpoint.ByPlayer)]
    public async Task Read_WithoutJwt_Returns401(ReadEndpoint endpoint)
    {
        var url = await SeedUrlForAsync(endpoint);

        var response = await GetAsync(url, token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(ReadEndpoint.ById)]
    [InlineData(ReadEndpoint.ByTeam)]
    [InlineData(ReadEndpoint.ByPlayer)]
    public async Task Read_AsUnlinkedIdentity_Returns403(ReadEndpoint endpoint)
    {
        var url = await SeedUrlForAsync(endpoint);

        var response = await GetAsync(url, LinkedPlayerTokens.ForSubject(LinkedPlayerTokens.NewSubject()));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Read_WithGuidSubjectEqualToUnlinkedPlayerId_Returns403OnAllEndpoints()
    {
        // Arrange: neither the applicant's nor the owner's Player.Id as a raw GUID subject is a link.
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);
        var applicantToken = TeamBuilderWebApplicationFactory.CreateTestJwt(player.Id.ToString());
        var ownerToken = TeamBuilderWebApplicationFactory.CreateTestJwt(team.OwnerId!.Value.ToString());

        // Act + Assert
        (await GetAsync(ById(jr.Id), applicantToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(ById(jr.Id), ownerToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(ByTeam(team.Id), ownerToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(ByPlayer(player.Id), applicantToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Read_AsLinkedPlayerWithNonGuidSubject_Returns200OnAllEndpoints()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var (team, player) = await SeedTeamAndPlayerAsync(ownerId: ownerId);
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);
        var applicantSubject = $"auth0|{Guid.NewGuid():N}";
        var ownerSubject = $"google-oauth2|{Guid.NewGuid():N}";
        await LinkedPlayerTokens.LinkAsync(_factory.Services, player.Id, applicantSubject, TeamBuilderWebApplicationFactory.TestIssuer);
        await LinkedPlayerTokens.LinkAsync(_factory.Services, ownerId, ownerSubject, TeamBuilderWebApplicationFactory.TestIssuer);
        var applicantToken = LinkedPlayerTokens.ForSubject(applicantSubject);
        var ownerToken = LinkedPlayerTokens.ForSubject(ownerSubject);

        // Act + Assert
        (await GetAsync(ById(jr.Id), applicantToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(ById(jr.Id), ownerToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(ByTeam(team.Id), ownerToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(ByPlayer(player.Id), applicantToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Read_SameSubjectUnderAnotherIssuer_DoesNotCrossAuthorize()
    {
        // Arrange: clearing Jwt:Issuer disables issuer validation so one host accepts both issuers.
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Issuer"] = "" })));
        var client = factory.CreateClient();
        const string ownerIssuer = "https://login.example.com/tenant-a/v2.0";
        const string otherIssuer = "https://login.example.com/tenant-b/v2.0";
        var subject = $"shared|{Guid.NewGuid():N}";
        var ownerId = Guid.NewGuid();
        var otherPlayerId = Guid.NewGuid();
        var (team, player) = await SeedTeamAndPlayerAsync(ownerId: ownerId);
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);
        await LinkedPlayerTokens.LinkAsync(factory.Services, ownerId, subject, ownerIssuer);
        await LinkedPlayerTokens.LinkAsync(factory.Services, otherPlayerId, subject, otherIssuer);
        var ownerToken = LinkedPlayerTokens.ForSubject(subject, ownerIssuer);
        var otherToken = LinkedPlayerTokens.ForSubject(subject, otherIssuer);
        var unlinkedToken = LinkedPlayerTokens.ForSubject(subject, "https://login.example.com/tenant-c/v2.0");

        // Act + Assert: the owner's subject under another issuer is a different (or no) player.
        (await GetAsync(ById(jr.Id), ownerToken, client)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(ByTeam(team.Id), ownerToken, client)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(ById(jr.Id), otherToken, client)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(ByTeam(team.Id), otherToken, client)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(ByPlayer(ownerId), otherToken, client)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(ById(jr.Id), unlinkedToken, client)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(ByTeam(team.Id), unlinkedToken, client)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── GET /api/v1/joinrequests/{id} ────────────────────────────────────────

    [Fact]
    public async Task GetById_AsApplicant_Returns200WithMessage()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedJoinRequestAsync(team.Id, player.Id, message: "Applicant note");

        // Act
        var response = await GetAsPlayerAsync(ById(jr.Id), player.Id);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<JoinRequestDto>();
        dto!.Id.Should().Be(jr.Id);
        dto.Status.Should().Be(RequestStatus.Pending);
        dto.Message.Should().Be("Applicant note");
        dto.TeamOwnerId.Should().Be(team.OwnerId);
    }

    [Fact]
    public async Task GetById_AsTeamOwner_Returns200WithMessage()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedJoinRequestAsync(team.Id, player.Id, message: "Owner sees this");

        // Act
        var response = await GetAsPlayerAsync(ById(jr.Id), team.OwnerId!.Value);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<JoinRequestDto>();
        dto!.Id.Should().Be(jr.Id);
        dto.PlayerId.Should().Be(player.Id);
        dto.Message.Should().Be("Owner sees this");
    }

    [Fact]
    public async Task GetById_AsUnrelatedLinkedPlayer_Returns403()
    {
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);

        var response = await GetAsPlayerAsync(ById(jr.Id), Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(TeamRole.Leader)]
    [InlineData(TeamRole.Member)]
    public async Task GetById_AsNonOwnerTeamMember_Returns403(TeamRole role)
    {
        var (team, player) = await SeedTeamAndPlayerAsync();
        var memberId = Guid.NewGuid();
        await AddTeamMemberAsync(team.Id, memberId, role);
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);

        var response = await GetAsPlayerAsync(ById(jr.Id), memberId);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetById_WhenJoinRequestDoesNotExist_Returns404()
    {
        var response = await GetAsPlayerAsync(ById(Guid.NewGuid()), Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetById_ProcessedRequest_DoesNotExposeProcessedByUserId()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedJoinRequestAsync(team.Id, player.Id, RequestStatus.Rejected, processedByUserId: team.OwnerId);

        // Act
        var response = await GetAsPlayerAsync(ById(jr.Id), player.Id);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContainEquivalentOf("processedBy");
        typeof(JoinRequestDto).GetProperty(nameof(JoinRequest.ProcessedByUserId)).Should().BeNull();
    }

    // ── GET /api/v1/joinrequests/teams/{teamId} ──────────────────────────────

    [Fact]
    public async Task GetByTeam_AsTeamOwner_Returns200WithTeamRequests()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedJoinRequestAsync(team.Id, player.Id, message: "hello owner");
        var (otherTeam, otherPlayer) = await SeedTeamAndPlayerAsync(ownerId: team.OwnerId);
        await SeedPendingJoinRequestAsync(otherTeam.Id, otherPlayer.Id);

        // Act
        var response = await GetAsPlayerAsync(ByTeam(team.Id), team.OwnerId!.Value);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<PaginatedResult<JoinRequestDto>>();
        page!.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle().Which.Should().Match<JoinRequestDto>(d =>
            d.Id == jr.Id && d.TeamId == team.Id && d.Message == "hello owner");
    }

    [Fact]
    public async Task GetByTeam_AsUnrelatedLinkedPlayer_Returns403()
    {
        var (team, player) = await SeedTeamAndPlayerAsync();
        await SeedPendingJoinRequestAsync(team.Id, player.Id);

        var response = await GetAsPlayerAsync(ByTeam(team.Id), Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetByTeam_AsApplicant_Returns403()
    {
        var (team, player) = await SeedTeamAndPlayerAsync();
        await SeedPendingJoinRequestAsync(team.Id, player.Id);

        var response = await GetAsPlayerAsync(ByTeam(team.Id), player.Id);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(TeamRole.Leader)]
    [InlineData(TeamRole.CoLeader)]
    [InlineData(TeamRole.Member)]
    public async Task GetByTeam_AsNonOwnerTeamMember_Returns403(TeamRole role)
    {
        var (team, player) = await SeedTeamAndPlayerAsync();
        var memberId = Guid.NewGuid();
        await AddTeamMemberAsync(team.Id, memberId, role);
        await SeedPendingJoinRequestAsync(team.Id, player.Id);

        var response = await GetAsPlayerAsync(ByTeam(team.Id), memberId);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetByTeam_WhenTeamDoesNotExist_Returns404()
    {
        var response = await GetAsPlayerAsync(ByTeam(Guid.NewGuid()), Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetByTeam_AsTeamOwner_PaginatesAndFiltersByStatus()
    {
        // Arrange: 3 pending + 2 rejected, newest first.
        var (team, _) = await SeedTeamAndPlayerAsync();
        var baseTime = DateTime.UtcNow.AddHours(-1);
        var pending = new List<JoinRequest>();
        for (var i = 0; i < 3; i++)
            pending.Add(await SeedJoinRequestAsync(team.Id, Guid.NewGuid(), requestedAtUtc: baseTime.AddMinutes(i)));
        for (var i = 0; i < 2; i++)
            await SeedJoinRequestAsync(team.Id, Guid.NewGuid(), RequestStatus.Rejected, requestedAtUtc: baseTime.AddMinutes(10 + i));

        // Act
        var ownerId = team.OwnerId!.Value;
        var all = await (await GetAsPlayerAsync(ByTeam(team.Id, "?page=1&pageSize=2"), ownerId)).Content.ReadFromJsonAsync<PaginatedResult<JoinRequestDto>>();
        var pendingPage2 = await (await GetAsPlayerAsync(ByTeam(team.Id, "?page=2&pageSize=2&status=Pending"), ownerId)).Content.ReadFromJsonAsync<PaginatedResult<JoinRequestDto>>();
        var rejected = await (await GetAsPlayerAsync(ByTeam(team.Id, $"?status={(int)RequestStatus.Rejected}"), ownerId)).Content.ReadFromJsonAsync<PaginatedResult<JoinRequestDto>>();

        // Assert
        all!.TotalCount.Should().Be(5);
        all.Page.Should().Be(1);
        all.PageSize.Should().Be(2);
        all.Items.Should().HaveCount(2).And.OnlyContain(d => d.Status == RequestStatus.Rejected);

        pendingPage2!.TotalCount.Should().Be(3);
        pendingPage2.Items.Should().ContainSingle().Which.Id.Should().Be(pending[0].Id);

        rejected!.TotalCount.Should().Be(2);
        rejected.Items.Should().HaveCount(2).And.OnlyContain(d => d.Status == RequestStatus.Rejected);
    }

    // ── GET /api/v1/joinrequests/players/{playerId} ──────────────────────────

    [Fact]
    public async Task GetByPlayer_OwnHistory_Returns200WithOnlyOwnRequests()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedJoinRequestAsync(team.Id, player.Id, message: "mine");
        var (_, otherPlayer) = await SeedTeamAndPlayerAsync();
        await SeedPendingJoinRequestAsync(team.Id, otherPlayer.Id);

        // Act
        var response = await GetAsPlayerAsync(ByPlayer(player.Id), player.Id);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<PaginatedResult<JoinRequestDto>>();
        page!.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle().Which.Should().Match<JoinRequestDto>(d =>
            d.Id == jr.Id && d.PlayerId == player.Id && d.Message == "mine");
    }

    [Fact]
    public async Task GetByPlayer_AnotherPlayersHistory_Returns403()
    {
        var (team, player) = await SeedTeamAndPlayerAsync();
        await SeedPendingJoinRequestAsync(team.Id, player.Id);

        var asStranger = await GetAsPlayerAsync(ByPlayer(player.Id), Guid.NewGuid());
        var asTeamOwner = await GetAsPlayerAsync(ByPlayer(player.Id), team.OwnerId!.Value);

        asStranger.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        asTeamOwner.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetByPlayer_NonexistentOtherPlayer_Returns403NotEmptyPage()
    {
        var response = await GetAsPlayerAsync(ByPlayer(Guid.NewGuid()), Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetByPlayer_OwnHistory_PaginatesAndFiltersByStatus()
    {
        // Arrange: 2 approved + 3 pending across distinct teams, newest first.
        var (_, player) = await SeedTeamAndPlayerAsync();
        var baseTime = DateTime.UtcNow.AddHours(-1);
        var approved = new List<JoinRequest>();
        for (var i = 0; i < 2; i++)
        {
            var (team, _) = await SeedTeamAndPlayerAsync();
            approved.Add(await SeedJoinRequestAsync(team.Id, player.Id, RequestStatus.Approved, requestedAtUtc: baseTime.AddMinutes(i)));
        }
        for (var i = 0; i < 3; i++)
        {
            var (team, _) = await SeedTeamAndPlayerAsync();
            await SeedJoinRequestAsync(team.Id, player.Id, requestedAtUtc: baseTime.AddMinutes(10 + i));
        }

        // Act
        var all = await (await GetAsPlayerAsync(ByPlayer(player.Id, "?page=3&pageSize=2"), player.Id)).Content.ReadFromJsonAsync<PaginatedResult<JoinRequestDto>>();
        var approvedOnly = await (await GetAsPlayerAsync(ByPlayer(player.Id, "?status=Approved&pageSize=1"), player.Id)).Content.ReadFromJsonAsync<PaginatedResult<JoinRequestDto>>();

        // Assert
        all!.TotalCount.Should().Be(5);
        all.Page.Should().Be(3);
        all.Items.Should().ContainSingle().Which.Id.Should().Be(approved[0].Id);

        approvedOnly!.TotalCount.Should().Be(2);
        approvedOnly.PageSize.Should().Be(1);
        approvedOnly.Items.Should().ContainSingle().Which.Id.Should().Be(approved[1].Id);
    }

    // ── POST /api/v1/joinrequests ────────────────────────────────────────────

    [Fact]
    public async Task Create_WithValidPayload_Returns201()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var dto = new CreateJoinRequestDto { TeamId = team.Id, Message = "Please let me join!" };
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, player.Id);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/joinrequests");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<JoinRequestDto>();
        created!.TeamId.Should().Be(team.Id);
        created.PlayerId.Should().Be(player.Id);
        created.Status.Should().Be(RequestStatus.Pending);
    }

    [Fact]
    public async Task Create_WithoutJwt_Returns401()
    {
        // Arrange
        var (team, _) = await SeedTeamAndPlayerAsync();
        var dto = new CreateJoinRequestDto { TeamId = team.Id };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/joinrequests", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_WithMissingSubjectClaim_Returns401AndDoesNotPersistJoinRequest()
    {
        // Arrange
        var (team, _) = await SeedTeamAndPlayerAsync();
        var before = await GetJoinRequestCountAsync();
        var dto = new CreateJoinRequestDto { TeamId = team.Id };
        var token = TeamBuilderWebApplicationFactory.CreateTestJwtWithSubject(null, includeSubject: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/joinrequests");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await GetJoinRequestCountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Create_DuplicatePendingRequest_Returns409Conflict()
    {
        // Arrange — seed an already-pending request for the same team/player
        var (team, player) = await SeedTeamAndPlayerAsync();
        await SeedPendingJoinRequestAsync(team.Id, player.Id);

        var dto = new CreateJoinRequestDto { TeamId = team.Id };
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, player.Id);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/joinrequests");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status409Conflict);
    }

    // ── PUT /api/v1/joinrequests/{id}/process ────────────────────────────────

    [Fact]
    public async Task Process_ApproveExistingRequestAsTeamOwner_Returns200WithApprovedStatus()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);
        var processDto = new ProcessJoinRequestDto { Status = RequestStatus.Approved };
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, team.OwnerId!.Value);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/joinrequests/{jr.Id}/process");
        request.Content = JsonContent.Create(processDto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JoinRequestDto>();
        result!.Status.Should().Be(RequestStatus.Approved);
    }

    [Fact]
    public async Task Process_RejectExistingRequestAsTeamOwner_Returns200WithRejectedStatus()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);
        var processDto = new ProcessJoinRequestDto { Status = RequestStatus.Rejected };
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, team.OwnerId!.Value);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/joinrequests/{jr.Id}/process");
        request.Content = JsonContent.Create(processDto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JoinRequestDto>();
        result!.Status.Should().Be(RequestStatus.Rejected);
    }

    [Fact]
    public async Task Process_ApproveFullTeam_Returns409Conflict()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync(maxMembers: 1, currentMemberCount: 1, status: TeamStatus.Full);
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);
        var processDto = new ProcessJoinRequestDto { Status = RequestStatus.Approved };
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, team.OwnerId!.Value);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/joinrequests/{jr.Id}/process");
        request.Content = JsonContent.Create(processDto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Process_WhenJoinRequestDoesNotExist_Returns404()
    {
        // Arrange
        var processDto = new ProcessJoinRequestDto { Status = RequestStatus.Approved };
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/joinrequests/{Guid.NewGuid()}/process");
        request.Content = JsonContent.Create(processDto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Process_WithoutJwt_Returns401()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);
        var processDto = new ProcessJoinRequestDto { Status = RequestStatus.Approved };

        // Act
        var response = await _client.PutAsJsonAsync($"/api/v1/joinrequests/{jr.Id}/process", processDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Process_ApproveAsTeamOwner_CreatesTeamMemberAndRecordsOwnerAsProcessor()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);

        // Act
        var response = await SendProcessAsync(jr.Id, RequestStatus.Approved, team.OwnerId!.Value);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (joinRequest, updatedTeam, teamMemberCount) = await GetProcessingStateAsync(jr.Id, team.Id);
        joinRequest.Status.Should().Be(RequestStatus.Approved);
        joinRequest.ProcessedByUserId.Should().Be(team.OwnerId);
        updatedTeam.CurrentMemberCount.Should().Be(1);
        teamMemberCount.Should().Be(1);
    }

    [Theory]
    [InlineData(RequestStatus.Approved)]
    [InlineData(RequestStatus.Rejected)]
    public async Task Process_AsUnrelatedAuthenticatedUser_Returns403AndLeavesStateUnchanged(RequestStatus status)
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);

        // Act
        var response = await SendProcessAsync(jr.Id, status, Guid.NewGuid());

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var (joinRequest, updatedTeam, teamMemberCount) = await GetProcessingStateAsync(jr.Id, team.Id);
        joinRequest.Status.Should().Be(RequestStatus.Pending);
        joinRequest.ProcessedAtUtc.Should().BeNull();
        joinRequest.ProcessedByUserId.Should().BeNull();
        updatedTeam.CurrentMemberCount.Should().Be(0);
        updatedTeam.Status.Should().Be(TeamStatus.Active);
        teamMemberCount.Should().Be(0);
    }

    [Fact]
    public async Task Process_AsApplicantWhoIsNotTeamOwner_Returns403AndDoesNotCreateTeamMember()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);

        // Act
        var response = await SendProcessAsync(jr.Id, RequestStatus.Approved, player.Id);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var (joinRequest, updatedTeam, teamMemberCount) = await GetProcessingStateAsync(jr.Id, team.Id);
        joinRequest.Status.Should().Be(RequestStatus.Pending);
        joinRequest.ProcessedByUserId.Should().BeNull();
        updatedTeam.CurrentMemberCount.Should().Be(0);
        teamMemberCount.Should().Be(0);
    }

    [Fact]
    public async Task Process_AsApplicantWhoIsAlsoTeamOwner_Returns200()
    {
        // Arrange
        var applicantOwnerId = Guid.NewGuid();
        var (team, _) = await SeedTeamAndPlayerAsync(ownerId: applicantOwnerId);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
            db.Players.Add(new Player
            {
                Id = applicantOwnerId,
                Username = $"owner-{Guid.NewGuid():N}",
                CreatedAtUtc = DateTime.UtcNow,
                RowVersion = []
            });
            await db.SaveChangesAsync();
        }
        var jr = await SeedPendingJoinRequestAsync(team.Id, applicantOwnerId);

        // Act
        var response = await SendProcessAsync(jr.Id, RequestStatus.Approved, applicantOwnerId);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (joinRequest, _, teamMemberCount) = await GetProcessingStateAsync(jr.Id, team.Id);
        joinRequest.Status.Should().Be(RequestStatus.Approved);
        teamMemberCount.Should().Be(1);
    }

    [Theory]
    [InlineData(TeamRole.Leader)]
    [InlineData(TeamRole.CoLeader)]
    [InlineData(TeamRole.Officer)]
    [InlineData(TeamRole.Member)]
    [InlineData(TeamRole.Recruit)]
    public async Task Process_AsNonOwnerTeamMemberWithAnyRole_Returns403(TeamRole role)
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var memberId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
            db.Players.Add(new Player
            {
                Id = memberId,
                Username = $"member-{Guid.NewGuid():N}",
                CreatedAtUtc = DateTime.UtcNow,
                RowVersion = []
            });
            db.TeamMembers.Add(new TeamMember
            {
                Id = Guid.NewGuid(),
                TeamId = team.Id,
                PlayerId = memberId,
                Role = role,
                JoinedAtUtc = DateTime.UtcNow,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
                RowVersion = []
            });
            // This member is the team's single active membership.
            var seededTeam = await db.Teams.SingleAsync(t => t.Id == team.Id);
            seededTeam.CurrentMemberCount = 1;
            await db.SaveChangesAsync();
        }
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);

        // Act
        var response = await SendProcessAsync(jr.Id, RequestStatus.Approved, memberId);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var (joinRequest, updatedTeam, teamMemberCount) = await GetProcessingStateAsync(jr.Id, team.Id);
        joinRequest.Status.Should().Be(RequestStatus.Pending);
        updatedTeam.CurrentMemberCount.Should().Be(1);
        teamMemberCount.Should().Be(1);
    }

    [Fact]
    public async Task Process_AsNonOwnerOnAlreadyProcessedRequest_Returns403()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);
        (await SendProcessAsync(jr.Id, RequestStatus.Rejected, team.OwnerId!.Value)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Act
        var response = await SendProcessAsync(jr.Id, RequestStatus.Approved, Guid.NewGuid());

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Process_AsTeamOwnerOnAlreadyProcessedRequest_Returns409Conflict()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);
        (await SendProcessAsync(jr.Id, RequestStatus.Rejected, team.OwnerId!.Value)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Act
        var response = await SendProcessAsync(jr.Id, RequestStatus.Approved, team.OwnerId!.Value);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Process_AsNonOwnerOnFullTeam_Returns403()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync(maxMembers: 1, currentMemberCount: 1, status: TeamStatus.Full);
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);

        // Act
        var response = await SendProcessAsync(jr.Id, RequestStatus.Approved, Guid.NewGuid());

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── external identity resolution (ExternalIdentity scheme + PlayerIdentity) ──

    private static HttpRequestMessage CreateRequest(Guid teamId, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/joinrequests")
        {
            Content = JsonContent.Create(new CreateJoinRequestDto { TeamId = teamId })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task Create_AsLinkedPlayerWithNonGuidSubject_StoresInternalPlayerId()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var subject = $"auth0|{Guid.NewGuid():N}";
        await LinkedPlayerTokens.LinkAsync(_factory.Services, player.Id, subject, TeamBuilderWebApplicationFactory.TestIssuer);

        // Act
        using var request = CreateRequest(team.Id, LinkedPlayerTokens.ForSubject(subject));
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<JoinRequestDto>();
        created!.PlayerId.Should().Be(player.Id);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        (await db.JoinRequests.AsNoTracking().SingleAsync(jr => jr.Id == created.Id)).PlayerId.Should().Be(player.Id);
    }

    [Fact]
    public async Task Create_AsUnlinkedIdentity_Returns403AndDoesNotPersistJoinRequest()
    {
        // Arrange
        var (team, _) = await SeedTeamAndPlayerAsync();
        var before = await GetJoinRequestCountAsync();

        // Act
        using var request = CreateRequest(team.Id, LinkedPlayerTokens.ForSubject(LinkedPlayerTokens.NewSubject()));
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetJoinRequestCountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Create_WithGuidSubjectMatchingUnlinkedPlayerId_Returns403()
    {
        // A GUID-shaped external subject without a PlayerIdentity link is not a player identity.
        var (team, player) = await SeedTeamAndPlayerAsync();
        var before = await GetJoinRequestCountAsync();

        // Act
        using var request = CreateRequest(team.Id, TeamBuilderWebApplicationFactory.CreateTestJwt(player.Id.ToString()));
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetJoinRequestCountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Process_AsUnlinkedIdentity_Returns403AndLeavesStateUnchanged()
    {
        // Arrange
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/joinrequests/{jr.Id}/process")
        {
            Content = JsonContent.Create(new ProcessJoinRequestDto { Status = RequestStatus.Approved })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", LinkedPlayerTokens.ForSubject(LinkedPlayerTokens.NewSubject()));

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var (joinRequest, updatedTeam, teamMemberCount) = await GetProcessingStateAsync(jr.Id, team.Id);
        joinRequest.Status.Should().Be(RequestStatus.Pending);
        joinRequest.ProcessedByUserId.Should().BeNull();
        updatedTeam.CurrentMemberCount.Should().Be(0);
        teamMemberCount.Should().Be(0);
    }

    [Fact]
    public async Task Process_WithGuidSubjectEqualToUnlinkedOwnerId_Returns403()
    {
        // Arrange: the owner's Player.Id as a raw GUID subject is not a link.
        var (team, player) = await SeedTeamAndPlayerAsync();
        var jr = await SeedPendingJoinRequestAsync(team.Id, player.Id);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/joinrequests/{jr.Id}/process")
        {
            Content = JsonContent.Create(new ProcessJoinRequestDto { Status = RequestStatus.Approved })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TeamBuilderWebApplicationFactory.CreateTestJwt(team.OwnerId!.Value.ToString()));

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetProcessingStateAsync(jr.Id, team.Id)).joinRequest.Status.Should().Be(RequestStatus.Pending);
    }

    [Fact]
    public async Task SameSubjectUnderDifferentIssuers_ResolvesToDifferentPlayers()
    {
        // Arrange: clearing Jwt:Issuer disables issuer validation so one host accepts both issuers.
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Issuer"] = "" })));
        var client = factory.CreateClient();
        const string firstIssuer = "https://login.example.com/tenant-a/v2.0";
        const string secondIssuer = "https://login.example.com/tenant-b/v2.0";
        var subject = $"shared|{Guid.NewGuid():N}";
        var (team, firstPlayer) = await SeedTeamAndPlayerAsync();
        var secondPlayerId = Guid.NewGuid();
        await LinkedPlayerTokens.LinkAsync(factory.Services, firstPlayer.Id, subject, firstIssuer);
        await LinkedPlayerTokens.LinkAsync(factory.Services, secondPlayerId, subject, secondIssuer);

        // Act
        using var first = CreateRequest(team.Id, LinkedPlayerTokens.ForSubject(subject, firstIssuer));
        var firstResponse = await client.SendAsync(first);
        using var second = CreateRequest(team.Id, LinkedPlayerTokens.ForSubject(subject, secondIssuer));
        var secondResponse = await client.SendAsync(second);
        using var unlinked = CreateRequest(team.Id, LinkedPlayerTokens.ForSubject(subject, "https://login.example.com/tenant-c/v2.0"));
        var unlinkedResponse = await client.SendAsync(unlinked);

        // Assert
        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        (await firstResponse.Content.ReadFromJsonAsync<JoinRequestDto>())!.PlayerId.Should().Be(firstPlayer.Id);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        (await secondResponse.Content.ReadFromJsonAsync<JoinRequestDto>())!.PlayerId.Should().Be(secondPlayerId);
        unlinkedResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
