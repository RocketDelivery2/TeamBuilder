using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;

using TeamBuilder.Tests.Application;

namespace TeamBuilder.Tests.Integration;

public sealed class EventsControllerIntegrationTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly TeamBuilderWebApplicationFactory _factory;

    public EventsControllerIntegrationTests(TeamBuilderWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<EventOccurrence> SeedEventAsync(string name = "Test Event", Guid? hostId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

        var ev = new EventOccurrence
        {
            Id = Guid.NewGuid(),
            Name = name,
            ScheduledStartUtc = DateTime.UtcNow.AddDays(7),
            Status = EventStatus.Planned,
            MaxParticipants = 32,
            HostId = hostId,
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        };

        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev;
    }

    private async Task<int> GetEventCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        return await db.Events.CountAsync();
    }

    private async Task<(Guid PlayerId, string Subject, string Token)> CreateLinkedIdentityAsync(
        Guid? playerId = null,
        string? subject = null,
        string? issuer = null)
    {
        playerId ??= Guid.NewGuid();
        subject ??= $"external-{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        db.Players.Add(new Player { Id = playerId.Value, Username = $"player-{Guid.NewGuid():N}" });
        db.PlayerIdentities.Add(new PlayerIdentity
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId.Value,
            Issuer = issuer ?? TeamBuilderWebApplicationFactory.TestIssuer,
            Subject = subject,
            Provider = "oidc"
        });
        await db.SaveChangesAsync();

        return (playerId.Value, subject, TeamBuilderWebApplicationFactory.CreateTestJwtWithSubject(subject));
    }

    private static string CreateUnlinkedIdentityToken()
        => TeamBuilderWebApplicationFactory.CreateTestJwtWithSubject($"unlinked-{Guid.NewGuid():N}");

    // ── GET /api/v1/events/{id} ───────────────────────────────────────────────

    [Fact]
    public async Task GetById_WhenEventExists_Returns200()
    {
        // Arrange
        var ev = await SeedEventAsync($"GetById-{Guid.NewGuid():N}");

        // Act
        var response = await _client.GetAsync($"/api/v1/events/{ev.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<EventDto>();
        dto!.Id.Should().Be(ev.Id);
    }

    [Fact]
    public async Task GetById_WhenEventDoesNotExist_Returns404()
    {
        // Act
        var response = await _client.GetAsync($"/api/v1/events/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /api/v1/events ────────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_Returns200WithPaginatedEnvelope()
    {
        // Arrange
        await SeedEventAsync($"List-{Guid.NewGuid():N}");

        // Act
        var response = await _client.GetAsync("/api/v1/events?page=1&pageSize=5");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<PaginatedResult<EventDto>>();
        result.Should().NotBeNull();
        result!.Items.Should().NotBeNull();
    }

    // ── POST /api/v1/events ───────────────────────────────────────────────────

    [Fact]
    public async Task Create_WithValidPayload_Returns201()
    {
        // Arrange
        var dto = new CreateEventDto
        {
            Name = $"Event-{Guid.NewGuid():N}",
            EventDateUtc = DateTime.UtcNow.AddDays(14),
            MaxParticipants = 64
        };
        var caller = await CreateLinkedIdentityAsync(subject: $"non-guid-subject-{Guid.NewGuid():N}");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/events");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<EventDto>();
        created!.Name.Should().Be(dto.Name);
        created.HostId.Should().Be(caller.PlayerId);
        response.Headers.Location.Should().NotBeNull();
    }

    [Fact]
    public async Task Create_WithoutJwt_Returns401()
    {
        // Arrange
        var dto = new CreateEventDto
        {
            Name = $"Unauth-{Guid.NewGuid():N}",
            EventDateUtc = DateTime.UtcNow.AddDays(14),
            MaxParticipants = 10
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/events", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_WithAuthenticatedUnlinkedIdentity_Returns403AndDoesNotPersistEvent()
    {
        // Arrange
        var before = await GetEventCountAsync();
        var dto = new CreateEventDto
        {
            Name = $"MissingClaim-{Guid.NewGuid():N}",
            EventDateUtc = DateTime.UtcNow.AddDays(14),
            MaxParticipants = 10
        };
        var token = CreateUnlinkedIdentityToken();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/events");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetEventCountAsync()).Should().Be(before);
    }

    // ── PUT /api/v1/events/{id} ───────────────────────────────────────────────

    [Fact]
    public async Task Update_WithValidPayload_Returns200()
    {
        // Arrange
        var hostId = Guid.NewGuid();
        var ev = await SeedEventAsync($"Update-{Guid.NewGuid():N}", hostId);
        var dto = new UpdateEventDto { Name = $"Updated-{Guid.NewGuid():N}" };
        var host = await CreateLinkedIdentityAsync(hostId);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/events/{ev.Id}");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.Token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Update_WithoutJwt_Returns401()
    {
        // Arrange
        var ev = await SeedEventAsync($"UpdateUnauth-{Guid.NewGuid():N}");
        var dto = new UpdateEventDto { Name = "Should Fail" };

        // Act
        var response = await _client.PutAsJsonAsync($"/api/v1/events/{ev.Id}", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Update_WhenEventDoesNotExist_Returns404()
    {
        // Arrange
        var dto = new UpdateEventDto { Name = "Ghost Event" };
        var caller = await CreateLinkedIdentityAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/events/{Guid.NewGuid()}");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /api/v1/events/{id} ────────────────────────────────────────────

    [Fact]
    public async Task Delete_WhenEventExists_Returns204()
    {
        // Arrange
        var hostId = Guid.NewGuid();
        var ev = await SeedEventAsync($"Del-{Guid.NewGuid():N}", hostId);
        var host = await CreateLinkedIdentityAsync(hostId);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/events/{ev.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.Token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_WithoutJwt_Returns401()
    {
        // Arrange
        var ev = await SeedEventAsync($"DelUnauth-{Guid.NewGuid():N}");

        // Act
        var response = await _client.DeleteAsync($"/api/v1/events/{ev.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Delete_WhenEventDoesNotExist_Returns404()
    {
        // Arrange
        var caller = await CreateLinkedIdentityAsync();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/events/{Guid.NewGuid()}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_ByNonHost_Returns403()
    {
        // Arrange — event seeded with a different host
        var hostId = Guid.NewGuid();
        var ev = await SeedEventAsync($"UpdNonHost-{Guid.NewGuid():N}", hostId);
        var dto = new UpdateEventDto { Name = "Non-host rename" };
        var nonHost = await CreateLinkedIdentityAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/events/{ev.Id}");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", nonHost.Token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Delete_ByNonHost_Returns403()
    {
        // Arrange — event seeded with a different host
        var hostId = Guid.NewGuid();
        var ev = await SeedEventAsync($"DelNonHost-{Guid.NewGuid():N}", hostId);
        var nonHost = await CreateLinkedIdentityAsync();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/events/{ev.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", nonHost.Token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Update_WhenHostIdIsNull_Returns409()
    {
        // Arrange — event seeded without a host (orphaned)
        var ev = await SeedEventAsync($"UpdOrphan-{Guid.NewGuid():N}", hostId: null);
        var dto = new UpdateEventDto { Name = "Orphan Rename" };
        var caller = await CreateLinkedIdentityAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/events/{ev.Id}");
        request.Content = JsonContent.Create(dto);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Delete_WhenHostIdIsNull_Returns409()
    {
        // Arrange — event seeded without a host (orphaned)
        var ev = await SeedEventAsync($"DelOrphan-{Guid.NewGuid():N}", hostId: null);
        var caller = await CreateLinkedIdentityAsync();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/events/{ev.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── POST /api/v1/events with TeamId (TB-AUTH-014) ─────────────────────────

    private async Task<Team> SeedTeamAsync(Guid ownerId, TeamStatus status = TeamStatus.Active)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();

        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = $"Team-{Guid.NewGuid():N}",
            LifecycleStatus = TeamSeeding.Lifecycle(status),
            IsAcceptingMembers = TeamSeeding.Accepting(status),
            MaxMembers = 10,
            OwnerId = ownerId,
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        };

        db.Teams.Add(team);
        await db.SaveChangesAsync();
        return team;
    }

    private async Task<Team> SeedTeamWithStateAsync(
        Guid ownerId,
        TeamLifecycleStatus lifecycleStatus,
        bool isAcceptingMembers,
        int currentMemberCount,
        int maxMembers)
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
            CurrentMemberCount = currentMemberCount,
            OwnerId = ownerId,
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        };

        db.Teams.Add(team);
        await db.SaveChangesAsync();
        return team;
    }

    private async Task AddMemberAsync(Guid teamId, Guid playerId, TeamRole role)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        db.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            PlayerId = playerId,
            Role = role,
            IsActive = true,
            JoinedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
            RowVersion = []
        });
        await db.SaveChangesAsync();
    }

    private async Task<EventOccurrence?> FindEventAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        return await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);
    }

    private async Task<int> GetTeamEventCountAsync(Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        return await db.Events.CountAsync(e => e.TeamId == teamId);
    }

    private static CreateEventDto NewCreateDto(Guid? teamId) => new()
    {
        Name = $"TeamEvent-{Guid.NewGuid():N}",
        EventDateUtc = DateTime.UtcNow.AddDays(14),
        MaxParticipants = 16,
        TeamId = teamId
    };

    private Task<HttpResponseMessage> PostEventAsync(object body, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/events")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Create_StandaloneWithNullTeamId_Returns201WithResolvedHostAndNoTeam()
    {
        var caller = await CreateLinkedIdentityAsync();

        var response = await PostEventAsync(NewCreateDto(teamId: null), caller.Token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<EventDto>();
        created!.TeamId.Should().BeNull();
        created.HostId.Should().Be(caller.PlayerId);
        var persisted = await FindEventAsync(created.Id);
        persisted!.HostId.Should().Be(caller.PlayerId);
        persisted.TeamId.Should().BeNull();
    }

    [Fact]
    public async Task Create_WithEmptyTeamId_Returns400AndDoesNotPersistEvent()
    {
        var caller = await CreateLinkedIdentityAsync();
        var before = await GetEventCountAsync();

        var response = await PostEventAsync(NewCreateDto(Guid.Empty), caller.Token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("TeamId");
        (await GetEventCountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Create_WithNonexistentTeamId_Returns404AndDoesNotPersistEvent()
    {
        var caller = await CreateLinkedIdentityAsync();
        var missingTeamId = Guid.NewGuid();

        var response = await PostEventAsync(NewCreateDto(missingTeamId), caller.Token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetTeamEventCountAsync(missingTeamId)).Should().Be(0);
    }

    [Theory]
    [InlineData(TeamStatus.Active)]
    [InlineData(TeamStatus.Recruiting)]
    [InlineData(TeamStatus.Full)]
    public async Task Create_ByTeamOwner_ForAllowedTeamStatus_Returns201WithTeamAndOwnerAsHost(TeamStatus status)
    {
        var owner = await CreateLinkedIdentityAsync();
        var team = await SeedTeamAsync(owner.PlayerId, status);

        var response = await PostEventAsync(NewCreateDto(team.Id), owner.Token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<EventDto>();
        created!.TeamId.Should().Be(team.Id);
        created.HostId.Should().Be(owner.PlayerId);
        created.HostId.Should().Be(team.OwnerId);
        var persisted = await FindEventAsync(created.Id);
        persisted!.TeamId.Should().Be(team.Id);
        persisted.HostId.Should().Be(team.OwnerId);
    }

    // Only the lifecycle gates team-associated events: recruitment policy and physical capacity
    // never affect scheduling.
    [Theory]
    [InlineData(true, 3, 10)]   // accepting, open slots
    [InlineData(false, 3, 10)]  // recruitment closed, open slots
    [InlineData(true, 5, 5)]    // accepting but physically full (legacy Full)
    [InlineData(false, 5, 5)]   // closed and full (legacy Full)
    public async Task Create_ByTeamOwner_ForActiveLifecycle_Returns201RegardlessOfRecruitmentOrCapacity(
        bool isAcceptingMembers,
        int currentMemberCount,
        int maxMembers)
    {
        var owner = await CreateLinkedIdentityAsync();
        var team = await SeedTeamWithStateAsync(
            owner.PlayerId, TeamLifecycleStatus.Active, isAcceptingMembers, currentMemberCount, maxMembers);

        var response = await PostEventAsync(NewCreateDto(team.Id), owner.Token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await GetTeamEventCountAsync(team.Id)).Should().Be(1);
    }

    [Theory]
    [InlineData(TeamLifecycleStatus.Inactive, 0)]
    [InlineData(TeamLifecycleStatus.Disbanded, 0)]
    [InlineData(TeamLifecycleStatus.Inactive, 4)]
    [InlineData(TeamLifecycleStatus.Disbanded, 4)]
    public async Task Create_ByTeamOwner_ForInactiveOrDisbandedLifecycle_Returns409(
        TeamLifecycleStatus lifecycleStatus,
        int currentMemberCount)
    {
        var owner = await CreateLinkedIdentityAsync();
        var team = await SeedTeamWithStateAsync(owner.PlayerId, lifecycleStatus, false, currentMemberCount, 4);

        var response = await PostEventAsync(NewCreateDto(team.Id), owner.Token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await GetTeamEventCountAsync(team.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Create_ByUnrelatedLinkedPlayer_ForTeam_Returns403AndDoesNotPersistEvent()
    {
        var ownerId = Guid.NewGuid();
        await CreateLinkedIdentityAsync(ownerId);
        var team = await SeedTeamAsync(ownerId);
        var stranger = await CreateLinkedIdentityAsync();

        var response = await PostEventAsync(NewCreateDto(team.Id), stranger.Token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetTeamEventCountAsync(team.Id)).Should().Be(0);
    }

    [Theory]
    [InlineData(TeamRole.Member)]
    [InlineData(TeamRole.Leader)]
    [InlineData(TeamRole.CoLeader)]
    [InlineData(TeamRole.Officer)]
    [InlineData(TeamRole.Recruit)]
    public async Task Create_ByActiveNonOwnerMember_ForTeam_Returns403RegardlessOfRole(TeamRole role)
    {
        var ownerId = Guid.NewGuid();
        await CreateLinkedIdentityAsync(ownerId);
        var team = await SeedTeamAsync(ownerId);
        var member = await CreateLinkedIdentityAsync();
        await AddMemberAsync(team.Id, member.PlayerId, role);

        var response = await PostEventAsync(NewCreateDto(team.Id), member.Token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetTeamEventCountAsync(team.Id)).Should().Be(0);
    }

    [Theory]
    [InlineData(TeamStatus.Inactive)]
    [InlineData(TeamStatus.Disbanded)]
    public async Task Create_ByTeamOwner_ForInactiveOrDisbandedTeam_Returns409AndDoesNotPersistEvent(TeamStatus status)
    {
        var owner = await CreateLinkedIdentityAsync();
        var team = await SeedTeamAsync(owner.PlayerId, status);

        var response = await PostEventAsync(NewCreateDto(team.Id), owner.Token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync())
            .Should().Contain("Events cannot be created for an inactive or disbanded team.");
        (await GetTeamEventCountAsync(team.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Create_ForTeam_WithUnlinkedIdentity_Returns403()
    {
        var ownerId = Guid.NewGuid();
        await CreateLinkedIdentityAsync(ownerId);
        var team = await SeedTeamAsync(ownerId);

        var response = await PostEventAsync(NewCreateDto(team.Id), CreateUnlinkedIdentityToken());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetTeamEventCountAsync(team.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Create_ForTeam_WithoutJwt_Returns401()
    {
        var team = await SeedTeamAsync(Guid.NewGuid());

        var response = await _client.PostAsJsonAsync("/api/v1/events", NewCreateDto(team.Id));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await GetTeamEventCountAsync(team.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Create_ByTeamOwner_WithNonGuidExternalSubject_ResolvesThroughPlayerIdentity()
    {
        var subject = $"auth0|owner-{Guid.NewGuid():N}";
        var owner = await CreateLinkedIdentityAsync(subject: subject);
        var team = await SeedTeamAsync(owner.PlayerId);

        var response = await PostEventAsync(NewCreateDto(team.Id), owner.Token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<EventDto>();
        created!.HostId.Should().Be(owner.PlayerId);
        created.TeamId.Should().Be(team.Id);
    }

    // ── TeamId immutability through PUT ───────────────────────────────────────

    [Fact]
    public void UpdateEventDto_HasNoTeamIdProperty()
    {
        typeof(UpdateEventDto).GetProperty(nameof(CreateEventDto.TeamId)).Should().BeNull();
    }

    [Fact]
    public async Task Update_ByHost_WithTeamIdInBody_DoesNotChangeTeamAssociation()
    {
        var owner = await CreateLinkedIdentityAsync();
        var team = await SeedTeamAsync(owner.PlayerId);
        var otherTeam = await SeedTeamAsync(owner.PlayerId);
        var createResponse = await PostEventAsync(NewCreateDto(team.Id), owner.Token);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<EventDto>();

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/events/{created!.Id}");
        request.Content = JsonContent.Create(new { name = "Renamed team event", teamId = otherTeam.Id });
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<EventDto>();
        updated!.Name.Should().Be("Renamed team event");
        updated.TeamId.Should().Be(team.Id);
        (await FindEventAsync(created.Id))!.TeamId.Should().Be(team.Id);
    }

    [Fact]
    public async Task Update_ByHost_StandaloneEvent_WithTeamIdInBody_StaysStandalone()
    {
        var host = await CreateLinkedIdentityAsync();
        var ev = await SeedEventAsync($"Standalone-{Guid.NewGuid():N}", host.PlayerId);
        var team = await SeedTeamAsync(host.PlayerId);

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/events/{ev.Id}");
        request.Content = JsonContent.Create(new { name = "Still standalone", teamId = team.Id });
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.Token);
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await FindEventAsync(ev.Id))!.TeamId.Should().BeNull();
    }

    // ── DELETE semantics for team events stay host-only ───────────────────────

    [Fact]
    public async Task Delete_TeamEvent_ByNonHostTeamMember_Returns403()
    {
        var owner = await CreateLinkedIdentityAsync();
        var team = await SeedTeamAsync(owner.PlayerId);
        var createResponse = await PostEventAsync(NewCreateDto(team.Id), owner.Token);
        var created = await createResponse.Content.ReadFromJsonAsync<EventDto>();
        var member = await CreateLinkedIdentityAsync();
        await AddMemberAsync(team.Id, member.PlayerId, TeamRole.Leader);

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/events/{created!.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", member.Token);
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await FindEventAsync(created.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_TeamEvent_ByHostOwner_Returns204()
    {
        var owner = await CreateLinkedIdentityAsync();
        var team = await SeedTeamAsync(owner.PlayerId);
        var createResponse = await PostEventAsync(NewCreateDto(team.Id), owner.Token);
        var created = await createResponse.Content.ReadFromJsonAsync<EventDto>();

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/events/{created!.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await FindEventAsync(created.Id)).Should().BeNull();
    }
}
