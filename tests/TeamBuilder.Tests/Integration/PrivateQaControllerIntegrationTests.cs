using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// HTTP contracts added for the private QA basketball client: atomic event + roster creation
/// (<c>POST /api/v1/events</c> with <c>rosterRequirements</c> / <c>hostParticipates</c>), the
/// occurrence detail read model (<c>GET /api/v1/events/{id}/detail</c>), hosted games in the
/// caller's schedule (<c>includeHosted</c>), host-transfer lifecycle limits, and the stable
/// codes of requirement creation and host assignment. Races are covered on real SQL Server.
/// </summary>
public sealed class PrivateQaControllerIntegrationTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private static readonly DateTime Wednesday8Pm = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);

    private readonly TeamBuilderWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PrivateQaControllerIntegrationTests(TeamBuilderWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── atomic event + initial roster ────────────────────────────────────────

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task CreateEvent_WithARequirement_StartsAtZeroOrOneOfTen(bool hostParticipates, int supply)
    {
        var (hostId, hostToken) = await NewLinkedPlayerAsync();

        var created = await CreatedEventAsync(hostToken, PickupBody(hostParticipates));

        var detail = await DetailAsync(created.Id, hostToken);
        (detail.RequiredCount, detail.SupplyCount, detail.OpenQuantity, detail.IsRosterReady).Should().Be((10, supply, 10 - supply, false));
        detail.Requirements.Should().ContainSingle().Which.RoleCode.Should().Be("participant");
        (detail.HostPlayerId, detail.IsHost).Should().Be(((Guid?)hostId, true));
        if (hostParticipates)
        {
            var me = detail.Participants.Should().ContainSingle().Subject;
            (me.PlayerId, me.IsHost, me.Status).Should().Be((hostId, true, RosterAssignmentStatus.Confirmed));
            (detail.MyAssignmentId, detail.MyAssignmentStatus).Should().Be((me.AssignmentId, (RosterAssignmentStatus?)RosterAssignmentStatus.Confirmed));
        }
        else
        {
            detail.Participants.Should().BeEmpty();
            detail.MyAssignmentId.Should().BeNull();
        }
    }

    [Fact]
    public async Task CreateEvent_WithoutRosterFields_KeepsTheExistingContract()
    {
        var (_, hostToken) = await NewLinkedPlayerAsync();

        var created = await CreatedEventAsync(hostToken, new { name = "Social", eventDateUtc = Wednesday8Pm });

        var detail = await DetailAsync(created.Id, hostToken);
        (detail.RequiredCount, detail.IsRosterReady, detail.Requirements.Count).Should().Be((0, false, 0));
        created.ScheduledEndUtc.Should().BeNull();
    }

    [Fact]
    public async Task CreateEvent_WithSeveralRequirements_PutsThePlayingHostOnTheNamedRole()
    {
        var (hostId, hostToken) = await NewLinkedPlayerAsync();

        var created = await CreatedEventAsync(hostToken, new
        {
            name = "League night",
            eventDateUtc = Wednesday8Pm,
            scheduledEndUtc = Wednesday8Pm.AddHours(2),
            rosterRequirements = new object[] { new { roleCode = "Guard", requiredCount = 4 }, new { roleCode = "center", requiredCount = 2 } },
            hostParticipates = true,
            hostRoleCode = "CENTER"
        });

        created.ScheduledEndUtc.Should().Be(Wednesday8Pm.AddHours(2));
        var detail = await DetailAsync(created.Id, hostToken);
        detail.Requirements.Select(r => (r.RoleCode, r.SupplyCount)).Should().BeEquivalentTo([("guard", 0), ("center", 1)]);
        detail.Participants.Single().PlayerId.Should().Be(hostId);
    }

    [Theory]
    [InlineData("hostOnly")]
    [InlineData("duplicateRole")]
    [InlineData("missingHostRole")]
    [InlineData("unknownHostRole")]
    [InlineData("zeroCount")]
    [InlineData("endBeforeStart")]
    [InlineData("badRoleCode")]
    public async Task CreateEvent_InvalidRoster_Is400_AndCreatesNothing(string scenario)
    {
        var (_, hostToken) = await NewLinkedPlayerAsync();
        var name = $"invalid-{Guid.NewGuid():N}";
        object body = scenario switch
        {
            "hostOnly" => new { name, eventDateUtc = Wednesday8Pm, hostParticipates = true },
            "duplicateRole" => new { name, eventDateUtc = Wednesday8Pm, rosterRequirements = new object[] { new { roleCode = "guard", requiredCount = 2 }, new { roleCode = " GUARD ", requiredCount = 2 } } },
            "missingHostRole" => new { name, eventDateUtc = Wednesday8Pm, hostParticipates = true, rosterRequirements = new object[] { new { roleCode = "guard", requiredCount = 2 }, new { roleCode = "center", requiredCount = 1 } } },
            "unknownHostRole" => new { name, eventDateUtc = Wednesday8Pm, hostParticipates = true, hostRoleCode = "goalie", rosterRequirements = new object[] { new { roleCode = "guard", requiredCount = 2 } } },
            "zeroCount" => new { name, eventDateUtc = Wednesday8Pm, rosterRequirements = new object[] { new { roleCode = "participant", requiredCount = 0 } } },
            "endBeforeStart" => new { name, eventDateUtc = Wednesday8Pm, scheduledEndUtc = Wednesday8Pm.AddMinutes(-1) },
            _ => new { name, eventDateUtc = Wednesday8Pm, rosterRequirements = new object[] { new { roleCode = "not a code!", requiredCount = 10 } } }
        };

        var response = await SendAsync(HttpMethod.Post, "/api/v1/events", hostToken, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        var expectedCode = scenario switch
        {
            "hostOnly" => "HostParticipationRequiresRequirement",
            "duplicateRole" => "DuplicateRequirementRoleInRequest",
            "missingHostRole" or "unknownHostRole" => "HostRoleCodeInvalid",
            _ => null
        };
        if (expectedCode is not null)
            (await CodeAsync(response)).Should().Be(expectedCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        (await db.Events.AnyAsync(e => e.Name == name)).Should().BeFalse();
    }

    // ── requirement creation and host assignment codes ───────────────────────

    [Fact]
    public async Task CreateRequirement_HostChecksCarryStableCodes()
    {
        var (_, hostToken) = await NewLinkedPlayerAsync();
        var created = await CreatedEventAsync(hostToken, new { name = "Run", eventDateUtc = Wednesday8Pm });
        var url = $"/api/v1/events/{created.Id}/roster/requirements";

        (await SendAsync(HttpMethod.Post, url, await NewPlayerTokenAsync(), new { requiredCount = 10 })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await MutateEventAsync(created.Id, e => e.Status = EventStatus.Cancelled);
        var closed = await SendAsync(HttpMethod.Post, url, hostToken, new { requiredCount = 10 });
        closed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(closed)).Should().Be("OccurrenceClosed");

        await MutateEventAsync(created.Id, e => { e.Status = EventStatus.Open; e.HostId = null; });
        var orphaned = await SendAsync(HttpMethod.Post, url, hostToken, new { requiredCount = 10 });
        orphaned.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(orphaned)).Should().Be("OccurrenceHasNoHost");
    }

    [Fact]
    public async Task HostAssignment_RequirementContract()
    {
        var (_, hostToken) = await NewLinkedPlayerAsync();
        var created = await CreatedEventAsync(hostToken, PickupBody(hostParticipates: false));
        var other = await CreatedEventAsync(hostToken, PickupBody(hostParticipates: false));
        var foreignRequirement = (await DetailAsync(other.Id, hostToken)).Requirements.Single().Id;
        var (playerId, _) = await NewLinkedPlayerAsync();
        var url = $"/api/v1/events/{created.Id}/roster/assignments";

        var missing = await SendAsync(HttpMethod.Post, url, hostToken, new { playerId });
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeAsync(missing)).Should().Be("RequirementIdRequired");

        var foreign = await SendAsync(HttpMethod.Post, url, hostToken, new { playerId, requirementId = foreignRequirement });
        foreign.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeAsync(foreign)).Should().Be("RequirementNotOnOccurrence");

        (await DetailAsync(created.Id, hostToken)).SupplyCount.Should().Be(0);
        (await DetailAsync(other.Id, hostToken)).SupplyCount.Should().Be(0);
    }

    // ── occurrence detail ────────────────────────────────────────────────────

    [Fact]
    public async Task Detail_IsPublic_ListsOnlyLiveParticipants_AndExposesNoIdentityData()
    {
        var (hostId, hostToken) = await NewLinkedPlayerAsync();
        await MutatePlayerAsync(hostId, p => { p.Email = "private@example.com"; p.DisplayName = "Hana Host"; });
        var created = await CreatedEventAsync(hostToken, PickupBody(hostParticipates: true));
        var requirementId = (await DetailAsync(created.Id, hostToken)).Requirements.Single().Id;
        var (_, leaverToken) = await NewLinkedPlayerAsync();
        var left = await ClaimAsync(leaverToken, created.Id, requirementId);
        (await SendAsync(HttpMethod.Post, $"/api/v1/events/{created.Id}/roster/assignments/{left.Id}/leave", leaverToken)).EnsureSuccessStatusCode();

        var response = await _client.GetAsync($"/api/v1/events/{created.Id}/detail");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("private@example.com").And.NotContainAny("issuer", "subject", "tenant", "provider", "email");
        var detail = JsonSerializer.Deserialize<OccurrenceDetailDto>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        detail.Participants.Should().ContainSingle().Which.DisplayName.Should().Be("Hana Host");
        (detail.SupplyCount, detail.OpenQuantity, detail.HostDisplayName).Should().Be((1, 9, "Hana Host"));
        (detail.IsHost, detail.MyAssignmentId).Should().Be((false, (Guid?)null), "an anonymous caller has no relationship");
        detail.Location.Should().Be("Rec center court 2");
        detail.Category.Should().Be("basketball");
    }

    [Fact]
    public async Task Detail_ReflectsCallerAndClosedState()
    {
        var (_, hostToken) = await NewLinkedPlayerAsync();
        var created = await CreatedEventAsync(hostToken, PickupBody(hostParticipates: false));
        var requirementId = (await DetailAsync(created.Id, hostToken)).Requirements.Single().Id;
        var (_, playerToken) = await NewLinkedPlayerAsync();
        var mine = await ClaimAsync(playerToken, created.Id, requirementId);

        var asPlayer = await DetailAsync(created.Id, playerToken);
        (asPlayer.IsHost, asPlayer.MyAssignmentId, asPlayer.MyRequirementId, asPlayer.AcceptsRosterChanges)
            .Should().Be((false, (Guid?)mine.Id, (Guid?)requirementId, true));

        await MutateEventAsync(created.Id, e => e.Status = EventStatus.Completed);
        (await DetailAsync(created.Id, playerToken)).AcceptsRosterChanges.Should().BeFalse();
        (await _client.GetAsync($"/api/v1/events/{Guid.NewGuid()}/detail")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── my hosted games ──────────────────────────────────────────────────────

    [Fact]
    public async Task MyOccurrences_IncludeHosted_AddsOrganizerOnlyGames_Once()
    {
        var (_, hostToken) = await NewLinkedPlayerAsync();
        var organizing = await CreatedEventAsync(hostToken, PickupBody(hostParticipates: false));
        var playing = await CreatedEventAsync(hostToken, PickupBody(hostParticipates: true));
        var from = Uri.EscapeDataString(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).ToString("O"));

        var byDefault = await MyOccurrencesAsync(hostToken, $"fromUtc={from}");
        byDefault.Items.Select(i => i.OccurrenceId).Should().Equal(playing.Id);

        var withHosted = await MyOccurrencesAsync(hostToken, $"fromUtc={from}&includeHosted=true");
        withHosted.Items.Select(i => i.OccurrenceId).Should().BeEquivalentTo([organizing.Id, playing.Id]);
        var organizerOnly = withHosted.Items.Single(i => i.OccurrenceId == organizing.Id);
        (organizerOnly.IsHost, organizerOnly.MyAssignmentId, organizerOnly.MyAssignmentStatus, organizerOnly.TotalRequiredCount, organizerOnly.TotalSupplyCount)
            .Should().Be((true, (Guid?)null, (RosterAssignmentStatus?)null, 10, 0));
        withHosted.Items.Single(i => i.OccurrenceId == playing.Id).MyAssignmentStatus.Should().Be(RosterAssignmentStatus.Confirmed);
    }

    // ── host transfer lifecycle ──────────────────────────────────────────────

    [Theory]
    [InlineData(EventStatus.Completed)]
    [InlineData(EventStatus.Cancelled)]
    [InlineData(EventStatus.Archived)]
    public async Task HostTransfer_OfAClosedOccurrence_Is409OccurrenceClosed(EventStatus status)
    {
        var (hostId, hostToken) = await NewLinkedPlayerAsync();
        var created = await CreatedEventAsync(hostToken, PickupBody(hostParticipates: false));
        await MutateEventAsync(created.Id, e => e.Status = status);
        var (targetId, _) = await NewLinkedPlayerAsync();

        var response = await SendAsync(HttpMethod.Post, $"/api/v1/events/{created.Id}/host/transfer", hostToken, new { newHostPlayerId = targetId });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(response)).Should().Be("OccurrenceClosed");
        (await DetailAsync(created.Id, hostToken)).HostPlayerId.Should().Be(hostId);

        // A non-host still gets 403 first.
        (await SendAsync(HttpMethod.Post, $"/api/v1/events/{created.Id}/host/transfer", await NewPlayerTokenAsync(), new { newHostPlayerId = targetId }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static object PickupBody(bool hostParticipates) => new
    {
        name = "Wednesday 8 PM pickup basketball",
        eventDateUtc = Wednesday8Pm,
        scheduledEndUtc = Wednesday8Pm.AddHours(2),
        category = "basketball",
        location = "Rec center court 2",
        rosterRequirements = new[] { new { roleCode = "participant", requiredCount = 10 } },
        hostParticipates
    };

    private async Task<EventDto> CreatedEventAsync(string token, object body)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/events", token, body);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<EventDto>())!;
    }

    private async Task<OccurrenceDetailDto> DetailAsync(Guid occurrenceId, string token)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/v1/events/{occurrenceId}/detail", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OccurrenceDetailDto>())!;
    }

    private async Task<RosterAssignmentDto> ClaimAsync(string token, Guid occurrenceId, Guid requirementId)
    {
        var response = await SendAsync(HttpMethod.Post, $"/api/v1/events/{occurrenceId}/roster/claims", token, new { requirementId });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RosterAssignmentDto>())!;
    }

    private async Task<PlayerOccurrencePageDto> MyOccurrencesAsync(string token, string query)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/v1/players/me/occurrences?{query}", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PlayerOccurrencePageDto>())!;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private Task<string> NewPlayerTokenAsync() => LinkedPlayerTokens.ForPlayerAsync(_factory.Services, Guid.NewGuid());

    private async Task<(Guid PlayerId, string Token)> NewLinkedPlayerAsync()
    {
        var playerId = Guid.NewGuid();
        return (playerId, await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, playerId));
    }

    private async Task MutateEventAsync(Guid occurrenceId, Action<EventOccurrence> mutate)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        mutate(await db.Events.SingleAsync(e => e.Id == occurrenceId));
        await db.SaveChangesAsync();
    }

    private async Task MutatePlayerAsync(Guid playerId, Action<Player> mutate)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        mutate(await db.Players.SingleAsync(p => p.Id == playerId));
        await db.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }
}
