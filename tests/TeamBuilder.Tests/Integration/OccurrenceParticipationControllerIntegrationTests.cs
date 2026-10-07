using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// Day-of-game participation lifecycle (<c>POST …/roster/assignments/{id}/check-in|activate|no-show</c>),
/// occurrence host transfer (<c>POST /api/v1/events/{id}/host/transfer</c>), the caller's own
/// schedule (<c>GET /api/v1/players/me/occurrences</c>) and history-preserving event delete:
/// authorization order, state model, codes, paging and privacy. Concurrency is covered on real
/// SQL Server by <see cref="OccurrenceParticipationSqlServerIntegrationTests"/>.
/// </summary>
public sealed class OccurrenceParticipationControllerIntegrationTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private readonly TeamBuilderWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public OccurrenceParticipationControllerIntegrationTests(TeamBuilderWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── lifecycle: authorization order ───────────────────────────────────────

    [Theory]
    [InlineData("check-in")]
    [InlineData("activate")]
    [InlineData("no-show")]
    public async Task Lifecycle_AuthorizationOrder(string action)
    {
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync();
        var playerToken = await NewPlayerTokenAsync();
        var held = await ClaimedAsync(playerToken, occurrenceId, requirementId);
        var url = ActionUrl(occurrenceId, held.Id, action);

        (await _client.PostAsync(url, null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Post, url, LinkedPlayerTokens.ForSubject(LinkedPlayerTokens.NewSubject()))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, ActionUrl(Guid.NewGuid(), held.Id, action), hostToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        // The participant themselves is not the host.
        (await SendAsync(HttpMethod.Post, url, playerToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, url, await NewPlayerTokenAsync())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, ActionUrl(occurrenceId, Guid.NewGuid(), action), hostToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // An assignment of another occurrence is indistinguishable from a missing one.
        var (otherHostToken, otherOccurrenceId, otherRequirementId) = await SeedPickupAsync();
        var foreign = await ClaimedAsync(await NewPlayerTokenAsync(), otherOccurrenceId, otherRequirementId);
        (await SendAsync(HttpMethod.Post, ActionUrl(occurrenceId, foreign.Id, action), hostToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _ = otherHostToken;

        (await AssignmentAsync(held.Id)).Status.Should().Be(RosterAssignmentStatus.Confirmed);
        (await AssignmentAsync(foreign.Id)).Status.Should().Be(RosterAssignmentStatus.Confirmed);
    }

    [Fact]
    public async Task Lifecycle_OrphanedOccurrence_Returns409OccurrenceHasNoHost()
    {
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync();
        var held = await ClaimedAsync(await NewPlayerTokenAsync(), occurrenceId, requirementId);
        await MutateEventAsync(occurrenceId, e => e.HostId = null);

        await ExpectConflictAsync(await ActAsync(hostToken, occurrenceId, held.Id, "check-in"), RosterConflictCodes.OccurrenceHasNoHost);
        await ExpectConflictAsync(await RemoveAsync(hostToken, occurrenceId, held.Id), RosterConflictCodes.OccurrenceHasNoHost);
    }

    [Theory]
    [InlineData(EventStatus.Completed)]
    [InlineData(EventStatus.Cancelled)]
    [InlineData(EventStatus.Archived)]
    public async Task Lifecycle_ClosedOccurrence_Returns409OccurrenceClosed_AndChangesNothing(EventStatus status)
    {
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync();
        var held = await ClaimedAsync(await NewPlayerTokenAsync(), occurrenceId, requirementId);
        await MutateEventAsync(occurrenceId, e => e.Status = status);

        foreach (var action in new[] { "check-in", "activate", "no-show" })
            await ExpectConflictAsync(await ActAsync(hostToken, occurrenceId, held.Id, action), RosterConflictCodes.OccurrenceClosed);

        (await AssignmentAsync(held.Id)).Status.Should().Be(RosterAssignmentStatus.Confirmed);
    }

    // ── lifecycle: state model ───────────────────────────────────────────────

    [Theory]
    [InlineData(EventStatus.Planned)]
    [InlineData(EventStatus.Open)]
    [InlineData(EventStatus.InProgress)]
    public async Task CheckIn_ThenActivate_KeepsTheSpot_AndStampsEachStep(EventStatus status)
    {
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync();
        var held = await ClaimedAsync(await NewPlayerTokenAsync(), occurrenceId, requirementId);
        await MutateEventAsync(occurrenceId, e => e.Status = status);

        var checkIn = await ActAsync(hostToken, occurrenceId, held.Id, "check-in");
        checkIn.StatusCode.Should().Be(HttpStatusCode.OK, await checkIn.Content.ReadAsStringAsync());
        var checkedIn = (await checkIn.Content.ReadFromJsonAsync<RosterAssignmentDto>())!;
        checkedIn.Status.Should().Be(RosterAssignmentStatus.CheckedIn);
        checkedIn.CheckedInAtUtc.Should().NotBeNull();
        await ExpectSummaryAsync(occurrenceId, supply: 1, open: 9, ready: false);

        var activate = await ActAsync(hostToken, occurrenceId, held.Id, "activate");
        activate.StatusCode.Should().Be(HttpStatusCode.OK);
        var active = (await activate.Content.ReadFromJsonAsync<RosterAssignmentDto>())!;
        (active.Status, active.ExitReason, active.DepartedAtUtc).Should().Be((RosterAssignmentStatus.Active, (RosterExitReason?)null, (DateTime?)null));
        active.CheckedInAtUtc.Should().Be(checkedIn.CheckedInAtUtc);
        active.ActivatedAtUtc.Should().NotBeNull();
        await ExpectSummaryAsync(occurrenceId, supply: 1, open: 9, ready: false);
        (await AssignmentsOfAsync(occurrenceId)).Should().ContainSingle();
    }

    [Theory]
    [InlineData(RosterAssignmentStatus.Confirmed)]
    [InlineData(RosterAssignmentStatus.CheckedIn)]
    public async Task NoShow_ReleasesTheSpot_KeepsTheRow_AndAllowsAReplacement(RosterAssignmentStatus from)
    {
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync(requiredCount: 2);
        var playerId = Guid.NewGuid();
        await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, playerId);
        var held = await SeedAssignmentAsync(occurrenceId, playerId, from, requirementId);
        await ClaimedAsync(await NewPlayerTokenAsync(), occurrenceId, requirementId);
        await ExpectSummaryAsync(occurrenceId, supply: 2, open: 0, ready: true, required: 2);

        var response = await ActAsync(hostToken, occurrenceId, held.Id, "no-show");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var noShow = (await response.Content.ReadFromJsonAsync<RosterAssignmentDto>())!;
        (noShow.Status, noShow.ExitReason).Should().Be((RosterAssignmentStatus.NoShow, RosterExitReason.NoShow));
        noShow.DepartedAtUtc.Should().NotBeNull();
        await ExpectSummaryAsync(occurrenceId, supply: 1, open: 1, ready: false, required: 2);

        var replacement = await ClaimAsync(await NewPlayerTokenAsync(), occurrenceId, new { requirementId, replacesAssignmentId = held.Id });
        replacement.StatusCode.Should().Be(HttpStatusCode.Created, await replacement.Content.ReadAsStringAsync());
        await ExpectSummaryAsync(occurrenceId, supply: 2, open: 0, ready: true, required: 2);
        var rows = await AssignmentsOfAsync(occurrenceId);
        rows.Should().HaveCount(3);
        rows.Single(a => a.Id == held.Id).Status.Should().Be(RosterAssignmentStatus.NoShow);
    }

    [Theory]
    [InlineData(RosterAssignmentStatus.Confirmed, "activate")]
    [InlineData(RosterAssignmentStatus.Reserved, "check-in")]
    [InlineData(RosterAssignmentStatus.Reserved, "activate")]
    [InlineData(RosterAssignmentStatus.Reserved, "no-show")]
    [InlineData(RosterAssignmentStatus.CheckedIn, "check-in")]
    [InlineData(RosterAssignmentStatus.Active, "check-in")]
    [InlineData(RosterAssignmentStatus.Active, "activate")]
    [InlineData(RosterAssignmentStatus.Active, "no-show")]
    public async Task Lifecycle_StepNotAllowedFromTheCurrentStatus_Returns409AssignmentTransitionInvalid(RosterAssignmentStatus from, string action)
    {
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync();
        var playerId = Guid.NewGuid();
        await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, playerId);
        var held = await SeedAssignmentAsync(occurrenceId, playerId, from, requirementId);

        await ExpectConflictAsync(await ActAsync(hostToken, occurrenceId, held.Id, action), RosterConflictCodes.AssignmentTransitionInvalid);

        (await AssignmentAsync(held.Id)).Status.Should().Be(from);
    }

    [Theory]
    [InlineData(RosterAssignmentStatus.Departed)]
    [InlineData(RosterAssignmentStatus.NoShow)]
    [InlineData(RosterAssignmentStatus.Cancelled)]
    public async Task Lifecycle_NeverResurrectsAnEndedAssignment(RosterAssignmentStatus terminal)
    {
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync();
        var playerId = Guid.NewGuid();
        await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, playerId);
        var held = await SeedAssignmentAsync(occurrenceId, playerId, terminal, requirementId);

        foreach (var action in new[] { "check-in", "activate", "no-show" })
            await ExpectConflictAsync(await ActAsync(hostToken, occurrenceId, held.Id, action), RosterConflictCodes.AssignmentEnded);

        (await AssignmentAsync(held.Id)).Status.Should().Be(terminal);
        await ExpectSummaryAsync(occurrenceId, supply: 0, open: 10, ready: false);
    }

    [Fact]
    public async Task Lifecycle_HostWhoPlays_ManagesTheirOwnAssignmentLikeAnyOther()
    {
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync();
        await ExpectSummaryAsync(occurrenceId, supply: 0, open: 10, ready: false);
        var own = await ClaimedAsync(hostToken, occurrenceId, requirementId);

        (await ActAsync(hostToken, occurrenceId, own.Id, "check-in")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ActAsync(hostToken, occurrenceId, own.Id, "activate")).StatusCode.Should().Be(HttpStatusCode.OK);
        await ExpectSummaryAsync(occurrenceId, supply: 1, open: 9, ready: false);
    }

    // ── host transfer ────────────────────────────────────────────────────────

    [Fact]
    public async Task Transfer_AuthorizationOrder()
    {
        var (hostToken, occurrenceId, _) = await SeedPickupAsync();
        var (targetId, _) = await NewLinkedPlayerAsync();
        var body = new { newHostPlayerId = targetId };

        (await _client.PostAsJsonAsync(TransferUrl(occurrenceId), body)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Post, TransferUrl(occurrenceId), LinkedPlayerTokens.ForSubject(LinkedPlayerTokens.NewSubject()), body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, TransferUrl(Guid.NewGuid()), hostToken, body)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Post, TransferUrl(occurrenceId), await NewPlayerTokenAsync(), body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // The target cannot take the event for themselves.
        var targetToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, targetId);
        (await SendAsync(HttpMethod.Post, TransferUrl(occurrenceId), targetToken, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await EventAsync(occurrenceId)).HostId.Should().NotBe(targetId);
    }

    [Fact]
    public async Task Transfer_OrphanedOccurrence_Returns409OccurrenceHasNoHost()
    {
        var (hostToken, occurrenceId, _) = await SeedPickupAsync();
        var (targetId, _) = await NewLinkedPlayerAsync();
        await MutateEventAsync(occurrenceId, e => e.HostId = null);

        await ExpectConflictAsync(
            await SendAsync(HttpMethod.Post, TransferUrl(occurrenceId), hostToken, new { newHostPlayerId = targetId }),
            RosterConflictCodes.OccurrenceHasNoHost);
    }

    [Fact]
    public async Task Transfer_InvalidTargets_AreRejected_AndTheHostStays()
    {
        var (hostToken, occurrenceId, _) = await SeedPickupAsync();
        var hostId = (await EventAsync(occurrenceId)).HostId;
        var unlinkedId = Guid.NewGuid();
        await SeedPlayerAsync(unlinkedId);

        (await SendAsync(HttpMethod.Post, TransferUrl(occurrenceId), hostToken, new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, TransferUrl(occurrenceId), hostToken, new { newHostPlayerId = Guid.Empty })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, TransferUrl(occurrenceId), hostToken, new { newHostPlayerId = Guid.NewGuid() })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await ExpectConflictAsync(
            await SendAsync(HttpMethod.Post, TransferUrl(occurrenceId), hostToken, new { newHostPlayerId = unlinkedId }),
            RosterConflictCodes.HostTransferTargetNotLinked);

        (await EventAsync(occurrenceId)).HostId.Should().Be(hostId);
    }

    [Fact]
    public async Task Transfer_MovesOnlyTheHost_AndAuthorityMovesWithIt()
    {
        var teamOwnerId = Guid.NewGuid();
        await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, teamOwnerId);
        var teamId = await SeedTeamAsync(teamOwnerId);
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync(teamId: teamId, status: EventStatus.Open);
        var (targetId, targetToken) = await NewLinkedPlayerAsync();
        // The target already plays; the old host plays too.
        var targetSpot = await ClaimedAsync(targetToken, occurrenceId, requirementId);
        var hostSpot = await ClaimedAsync(hostToken, occurrenceId, requirementId);
        var other = await ClaimedAsync(await NewPlayerTokenAsync(), occurrenceId, requirementId);
        var before = await SummaryAsync(occurrenceId);

        var response = await SendAsync(HttpMethod.Post, TransferUrl(occurrenceId), hostToken, new { newHostPlayerId = targetId });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var dto = (await response.Content.ReadFromJsonAsync<EventDto>())!;
        (dto.HostId, dto.TeamId, dto.Status).Should().Be((targetId, teamId, EventStatus.Open));
        dto.HostUsername.Should().NotBeNullOrEmpty();
        var after = await SummaryAsync(occurrenceId);
        after.Should().BeEquivalentTo(before);
        (await AssignmentsOfAsync(occurrenceId)).Should().HaveCount(3);
        var stored = await EventAsync(occurrenceId);
        (stored.HostId, stored.TeamId, stored.Status, stored.IsDetached).Should().Be((targetId, teamId, EventStatus.Open, false));

        // Old host: no host-only action any more, but still a participant.
        (await ActAsync(hostToken, occurrenceId, other.Id, "check-in")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await RemoveAsync(hostToken, occurrenceId, other.Id)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, TransferUrl(occurrenceId), hostToken, new { newHostPlayerId = targetId })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Put, $"/api/v1/events/{occurrenceId}", hostToken, new { name = "hijack" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await LeaveAsync(hostToken, occurrenceId, hostSpot.Id)).StatusCode.Should().Be(HttpStatusCode.OK);

        // New host: full management, including of their own spot.
        (await ActAsync(targetToken, occurrenceId, other.Id, "check-in")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ActAsync(targetToken, occurrenceId, targetSpot.Id, "check-in")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Put, $"/api/v1/events/{occurrenceId}", targetToken, new { name = "Wednesday run (new host)" })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Transfer_ToYourself_IsANoOp()
    {
        var (hostToken, occurrenceId, _) = await SeedPickupAsync();
        var hostId = (await EventAsync(occurrenceId)).HostId!.Value;

        var response = await SendAsync(HttpMethod.Post, TransferUrl(occurrenceId), hostToken, new { newHostPlayerId = hostId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await EventAsync(occurrenceId)).UpdatedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Transfer_OfASeriesOccurrence_ChangesOnlyThatOccurrence()
    {
        var hostId = Guid.NewGuid();
        var hostToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, hostId);
        var (seriesId, first, second) = await SeedSeriesWithTwoOccurrencesAsync(hostId);
        var (targetId, _) = await NewLinkedPlayerAsync();

        var response = await SendAsync(HttpMethod.Post, TransferUrl(first), hostToken, new { newHostPlayerId = targetId });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var transferred = await EventAsync(first);
        (transferred.HostId, transferred.SeriesId, transferred.IsDetached, transferred.Status).Should().Be((targetId, seriesId, true, EventStatus.Planned));
        var sibling = await EventAsync(second);
        (sibling.HostId, sibling.IsDetached).Should().Be((hostId, false));
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        (await db.EventSeries.AsNoTracking().SingleAsync(s => s.Id == seriesId)).HostId.Should().Be(hostId);
    }

    // ── my occurrences ───────────────────────────────────────────────────────

    [Fact]
    public async Task MyOccurrences_RequiresALinkedPlayer()
    {
        (await _client.GetAsync(MyOccurrencesUrl())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Get, MyOccurrencesUrl(), LinkedPlayerTokens.ForSubject(LinkedPlayerTokens.NewSubject()))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MyOccurrences_ListsLiveCommitments_NearestFirst_WithEverythingAThinClientNeeds()
    {
        var (meId, meToken) = await NewLinkedPlayerAsync();
        var hostId = Guid.NewGuid();
        var hostToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, hostId);
        await MutatePlayerAsync(hostId, p => p.DisplayName = "Coach Kim");

        var later = await SeedEventAsync(hostId, EventStatus.Open, DateTime.UtcNow.AddDays(9));
        var sooner = await SeedEventAsync(hostId, EventStatus.Planned, DateTime.UtcNow.AddDays(2), location: "Rec center court 2", category: "basketball");
        var past = await SeedEventAsync(hostId, EventStatus.Open, DateTime.UtcNow.AddDays(-3));
        var left = await SeedEventAsync(hostId, EventStatus.Open, DateTime.UtcNow.AddDays(4));
        var notMine = await SeedEventAsync(hostId, EventStatus.Open, DateTime.UtcNow.AddDays(1));
        var soonerRequirement = await CreateRequirementAsync(hostToken, sooner, 10);
        await CreateRequirementAsync(hostToken, sooner, 2, "referee");
        var laterRequirement = await CreateRequirementAsync(hostToken, later, 2, "guard");
        var pastRequirement = await CreateRequirementAsync(hostToken, past, 10);
        var leftRequirement = await CreateRequirementAsync(hostToken, left, 10);
        var notMineRequirement = await CreateRequirementAsync(hostToken, notMine, 10);

        var soonerSpot = await ClaimedAsync(meToken, sooner, soonerRequirement);
        await ClaimedAsync(await NewPlayerTokenAsync(), sooner, soonerRequirement);
        await ClaimedAsync(meToken, later, laterRequirement);
        await ClaimedAsync(await NewPlayerTokenAsync(), later, laterRequirement);
        await SeedAssignmentAsync(past, meId, RosterAssignmentStatus.Confirmed, pastRequirement);
        var leftSpot = await ClaimedAsync(meToken, left, leftRequirement);
        (await LeaveAsync(meToken, left, leftSpot.Id)).StatusCode.Should().Be(HttpStatusCode.OK);
        await ClaimedAsync(await NewPlayerTokenAsync(), notMine, notMineRequirement);
        (await ActAsync(hostToken, sooner, soonerSpot.Id, "check-in")).StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await MyOccurrencesAsync(meToken);

        page.Items.Select(i => i.OccurrenceId).Should().Equal(sooner, later);
        page.NextCursor.Should().BeNull();
        var first = page.Items[0];
        first.Name.Should().Be("Wednesday 8 PM pickup");
        (first.Status, first.Location, first.Category, first.TeamId, first.SeriesId, first.VenueId)
            .Should().Be((EventStatus.Planned, "Rec center court 2", "basketball", (Guid?)null, (Guid?)null, (Guid?)null));
        first.ScheduledStartUtc.Kind.Should().Be(DateTimeKind.Utc);
        (first.HostPlayerId, first.HostDisplayName, first.IsHost).Should().Be((hostId, "Coach Kim", false));
        first.HostUsername.Should().NotBeNullOrEmpty();
        (first.MyAssignmentId, first.MyAssignmentStatus, first.MyRoleCode, first.MyRequirementId)
            .Should().Be((soonerSpot.Id, RosterAssignmentStatus.CheckedIn, "participant", soonerRequirement));
        (first.RequiredCount, first.SupplyCount, first.OpenQuantity, first.IsRosterReady).Should().Be((10, 2, 8, false));
        var second = page.Items[1];
        (second.MyRoleCode, second.RequiredCount, second.SupplyCount, second.OpenQuantity, second.IsRosterReady)
            .Should().Be(("guard", 2, 2, 0, true));
    }

    [Fact]
    public async Task MyOccurrences_IncludeTerminal_AddsEndedParticipation_WithTheLatestRow()
    {
        var (_, meToken) = await NewLinkedPlayerAsync();
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync();
        var firstSpot = await ClaimedAsync(meToken, occurrenceId, requirementId);
        (await LeaveAsync(meToken, occurrenceId, firstSpot.Id)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await MyOccurrencesAsync(meToken)).Items.Should().BeEmpty();
        var withHistory = await MyOccurrencesAsync(meToken, "includeTerminal=true");
        withHistory.Items.Should().ContainSingle().Which.MyAssignmentStatus.Should().Be(RosterAssignmentStatus.Cancelled);

        // Back in: the live row wins over the historical one.
        var secondSpot = await ClaimedAsync(meToken, occurrenceId, requirementId);
        var item = (await MyOccurrencesAsync(meToken, "includeTerminal=true")).Items.Should().ContainSingle().Subject;
        (item.MyAssignmentId, item.MyAssignmentStatus).Should().Be((secondSpot.Id, RosterAssignmentStatus.Confirmed));
        _ = hostToken;
    }

    [Fact]
    public async Task MyOccurrences_HostFlag_AndHostWithoutASpotIsNotListed()
    {
        var hostId = Guid.NewGuid();
        var hostToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, hostId);
        var organizing = await SeedEventAsync(hostId, EventStatus.Open, DateTime.UtcNow.AddDays(1));
        await CreateRequirementAsync(hostToken, organizing, 10);
        var playing = await SeedEventAsync(hostId, EventStatus.Open, DateTime.UtcNow.AddDays(2));
        await ClaimedAsync(hostToken, playing, await CreateRequirementAsync(hostToken, playing, 10));

        var page = await MyOccurrencesAsync(hostToken);

        page.Items.Should().ContainSingle().Which.Should().Match<PlayerOccurrenceDto>(i => i.OccurrenceId == playing && i.IsHost);
    }

    [Fact]
    public async Task MyOccurrences_InProgressGameStaysListedAfterItsStart()
    {
        var (_, meToken) = await NewLinkedPlayerAsync();
        var hostId = Guid.NewGuid();
        var hostToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, hostId);
        var running = await SeedEventAsync(hostId, EventStatus.InProgress, DateTime.UtcNow.AddMinutes(-30));
        var stale = await SeedEventAsync(hostId, EventStatus.Open, DateTime.UtcNow.AddMinutes(-30));
        var withEnd = await SeedEventAsync(hostId, EventStatus.Open, DateTime.UtcNow.AddMinutes(-30), endUtc: DateTime.UtcNow.AddMinutes(60));
        foreach (var id in new[] { running, stale, withEnd })
            await ClaimedAsync(meToken, id, await CreateRequirementAsync(hostToken, id, 10));

        var page = await MyOccurrencesAsync(meToken);

        page.Items.Select(i => i.OccurrenceId).Should().BeEquivalentTo([running, withEnd]);
    }

    [Fact]
    public async Task MyOccurrences_Window_FiltersAndValidates()
    {
        var (_, meToken) = await NewLinkedPlayerAsync();
        var hostId = Guid.NewGuid();
        var hostToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, hostId);
        var baseUtc = new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var ids = new List<Guid>();
        for (var day = 0; day < 4; day++)
        {
            var id = await SeedEventAsync(hostId, EventStatus.Open, baseUtc.AddDays(day));
            await ClaimedAsync(meToken, id, await CreateRequirementAsync(hostToken, id, 10));
            ids.Add(id);
        }

        var window = await MyOccurrencesAsync(meToken, $"fromUtc={Iso(baseUtc.AddDays(1))}&toUtc={Iso(baseUtc.AddDays(3))}");
        window.Items.Select(i => i.OccurrenceId).Should().Equal(ids[1], ids[2]);

        (await SendAsync(HttpMethod.Get, MyOccurrencesUrl($"fromUtc={Iso(baseUtc)}&toUtc={Iso(baseUtc.AddDays(-1))}"), meToken))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Get, MyOccurrencesUrl("cursor=not-a-cursor"), meToken))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task MyOccurrences_CursorPaging_IsDeterministic_EvenWithTiedStartTimes()
    {
        var (_, meToken) = await NewLinkedPlayerAsync();
        var hostId = Guid.NewGuid();
        var hostToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, hostId);
        var start = DateTime.UtcNow.AddDays(3);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            // Three games at exactly the same instant, then two later ones.
            var id = await SeedEventAsync(hostId, EventStatus.Open, i < 3 ? start : start.AddHours(i));
            await ClaimedAsync(meToken, id, await CreateRequirementAsync(hostToken, id, 10));
            ids.Add(id);
        }

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await MyOccurrencesAsync(meToken, cursor is null ? "pageSize=2" : $"pageSize=2&cursor={cursor}");
            page.Items.Count.Should().BeLessThanOrEqualTo(2);
            seen.AddRange(page.Items.Select(i => i.OccurrenceId));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        pages.Should().Be(3);
        seen.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(ids);
        seen.Skip(3).Should().Equal(ids[3], ids[4]);
        (await MyOccurrencesAsync(meToken, "pageSize=100")).Items.Select(i => i.OccurrenceId).Should().Equal(seen);
    }

    [Fact]
    public async Task MyOccurrences_ExposesNoPrivateIdentityData()
    {
        var meId = Guid.NewGuid();
        var meToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, meId);
        await MutatePlayerAsync(meId, p => p.Email = "me-private@example.com");
        var hostId = Guid.NewGuid();
        var hostToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, hostId);
        await MutatePlayerAsync(hostId, p => p.Email = "host-private@example.com");
        var occurrenceId = await SeedEventAsync(hostId, EventStatus.Open, DateTime.UtcNow.AddDays(1));
        await ClaimedAsync(meToken, occurrenceId, await CreateRequirementAsync(hostToken, occurrenceId, 10));

        var response = await SendAsync(HttpMethod.Get, MyOccurrencesUrl(), meToken);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotContain("private@example.com");
        body.Should().NotContain("ext|");
        foreach (var field in new[] { "email", "issuer", "subject", "provider", "tenant" })
            body.Should().NotContainEquivalentOf($"\"{field}");
    }

    [Fact]
    public async Task MyOccurrences_IncludesTeamEventsAndPickupGamesAlike_WithoutTeamMembership()
    {
        var (_, meToken) = await NewLinkedPlayerAsync();
        var ownerId = Guid.NewGuid();
        var ownerToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, ownerId);
        var teamId = await SeedTeamAsync(ownerId);
        var teamEvent = await SeedEventAsync(ownerId, EventStatus.Open, DateTime.UtcNow.AddDays(1), teamId: teamId);
        var pickup = await SeedEventAsync(ownerId, EventStatus.Open, DateTime.UtcNow.AddDays(2));
        await ClaimedAsync(meToken, teamEvent, await CreateRequirementAsync(ownerToken, teamEvent, 10));
        await ClaimedAsync(meToken, pickup, await CreateRequirementAsync(ownerToken, pickup, 10));

        var page = await MyOccurrencesAsync(meToken);

        page.Items.Select(i => (i.OccurrenceId, i.TeamId)).Should().Equal((teamEvent, teamId), (pickup, null));
    }

    // ── delete keeps history ─────────────────────────────────────────────────

    [Fact]
    public async Task DeleteEvent_WithParticipationHistory_Returns409_AndKeepsTheRows()
    {
        var (hostToken, occurrenceId, requirementId) = await SeedPickupAsync();
        var playerToken = await NewPlayerTokenAsync();
        var spot = await ClaimedAsync(playerToken, occurrenceId, requirementId);
        (await LeaveAsync(playerToken, occurrenceId, spot.Id)).StatusCode.Should().Be(HttpStatusCode.OK);

        await ExpectConflictAsync(
            await SendAsync(HttpMethod.Delete, $"/api/v1/events/{occurrenceId}", hostToken),
            RosterConflictCodes.OccurrenceHasParticipationHistory);

        (await _client.GetAsync($"/api/v1/events/{occurrenceId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AssignmentsOfAsync(occurrenceId)).Should().ContainSingle();

        // Cancelling is the supported way out, and it keeps the history too.
        (await SendAsync(HttpMethod.Put, $"/api/v1/events/{occurrenceId}", hostToken, new { status = EventStatus.Cancelled })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AssignmentsOfAsync(occurrenceId)).Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteEvent_WithOnlyRequirements_StillSucceeds()
    {
        var (hostToken, occurrenceId, _) = await SeedPickupAsync();

        (await SendAsync(HttpMethod.Delete, $"/api/v1/events/{occurrenceId}", hostToken)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.GetAsync($"/api/v1/events/{occurrenceId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string RosterUrl(Guid occurrenceId) => $"/api/v1/events/{occurrenceId}/roster";
    private static string ActionUrl(Guid occurrenceId, Guid assignmentId, string action) => $"{RosterUrl(occurrenceId)}/assignments/{assignmentId}/{action}";
    private static string TransferUrl(Guid occurrenceId) => $"/api/v1/events/{occurrenceId}/host/transfer";
    private static string MyOccurrencesUrl(string? query = null) => query is null ? "/api/v1/players/me/occurrences" : $"/api/v1/players/me/occurrences?{query}";
    private static string Iso(DateTime value) => Uri.EscapeDataString(value.ToString("O"));

    private Task<HttpResponseMessage> ActAsync(string token, Guid occurrenceId, Guid assignmentId, string action) =>
        SendAsync(HttpMethod.Post, ActionUrl(occurrenceId, assignmentId, action), token);

    private Task<HttpResponseMessage> ClaimAsync(string token, Guid occurrenceId, object body) =>
        SendAsync(HttpMethod.Post, $"{RosterUrl(occurrenceId)}/claims", token, body);

    private Task<HttpResponseMessage> LeaveAsync(string token, Guid occurrenceId, Guid assignmentId) =>
        SendAsync(HttpMethod.Post, ActionUrl(occurrenceId, assignmentId, "leave"), token);

    private Task<HttpResponseMessage> RemoveAsync(string token, Guid occurrenceId, Guid assignmentId) =>
        SendAsync(HttpMethod.Post, ActionUrl(occurrenceId, assignmentId, "remove"), token);

    private async Task<RosterAssignmentDto> ClaimedAsync(string token, Guid occurrenceId, Guid requirementId)
    {
        var response = await ClaimAsync(token, occurrenceId, new { requirementId });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RosterAssignmentDto>())!;
    }

    private async Task<PlayerOccurrencePageDto> MyOccurrencesAsync(string token, string? query = null)
    {
        var response = await SendAsync(HttpMethod.Get, MyOccurrencesUrl(query), token);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PlayerOccurrencePageDto>())!;
    }

    private static async Task ExpectConflictAsync(HttpResponseMessage response, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("code").GetString().Should().Be(code);
    }

    private async Task<RosterSummaryDto> SummaryAsync(Guid occurrenceId) =>
        (await _client.GetFromJsonAsync<RosterSummaryDto>(RosterUrl(occurrenceId)))!;

    private async Task ExpectSummaryAsync(Guid occurrenceId, int supply, int open, bool ready, int required = 10)
    {
        var summary = await SummaryAsync(occurrenceId);
        (summary.RequiredCount, summary.SupplyCount, summary.OpenQuantity, summary.IsRosterReady)
            .Should().Be((required, supply, open, ready));
    }

    private Task<string> NewPlayerTokenAsync() => LinkedPlayerTokens.ForPlayerAsync(_factory.Services, Guid.NewGuid());

    private async Task<(Guid PlayerId, string Token)> NewLinkedPlayerAsync()
    {
        var playerId = Guid.NewGuid();
        return (playerId, await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, playerId));
    }

    private async Task<(string HostToken, Guid OccurrenceId, Guid RequirementId)> SeedPickupAsync(
        int requiredCount = 10,
        Guid? teamId = null,
        EventStatus status = EventStatus.Planned)
    {
        var hostId = Guid.NewGuid();
        var hostToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, hostId);
        var occurrenceId = await SeedEventAsync(hostId, status, DateTime.UtcNow.AddDays(2), teamId: teamId);
        var requirementId = await CreateRequirementAsync(hostToken, occurrenceId, requiredCount);
        return (hostToken, occurrenceId, requirementId);
    }

    private async Task<Guid> CreateRequirementAsync(string hostToken, Guid occurrenceId, int requiredCount, string roleCode = "participant")
    {
        var response = await SendAsync(HttpMethod.Post, $"{RosterUrl(occurrenceId)}/requirements", hostToken, new { roleCode, requiredCount });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RosterRequirementDto>())!.Id;
    }

    private async Task<Guid> SeedEventAsync(
        Guid hostId,
        EventStatus status,
        DateTime startUtc,
        DateTime? endUtc = null,
        Guid? teamId = null,
        string? location = null,
        string? category = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var occurrence = new EventOccurrence
        {
            Id = Guid.NewGuid(),
            Name = "Wednesday 8 PM pickup",
            ScheduledStartUtc = startUtc,
            ScheduledEndUtc = endUtc,
            Status = status,
            MaxParticipants = 10,
            HostId = hostId,
            TeamId = teamId,
            LegacyLocation = location,
            Category = category,
            RowVersion = []
        };
        db.Events.Add(occurrence);
        await db.SaveChangesAsync();
        return occurrence.Id;
    }

    private async Task<(Guid SeriesId, Guid First, Guid Second)> SeedSeriesWithTwoOccurrencesAsync(Guid hostId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var series = new EventSeries
        {
            Id = Guid.NewGuid(),
            Name = "Wednesday run",
            HostId = hostId,
            LocalStartTime = new TimeOnly(20, 0),
            DurationMinutes = 90,
            TimeZoneId = "America/New_York",
            RecurrenceRule = "FREQ=WEEKLY;BYDAY=WE",
            MaxParticipants = 10,
            SeriesStartDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = EventSeriesStatus.Active,
            RowVersion = []
        };
        db.EventSeries.Add(series);
        var occurrences = Enumerable.Range(0, 2).Select(i => new EventOccurrence
        {
            Id = Guid.NewGuid(),
            Name = "Wednesday run",
            ScheduledStartUtc = DateTime.UtcNow.AddDays(7 * (i + 1)),
            Status = EventStatus.Planned,
            MaxParticipants = 10,
            HostId = hostId,
            SeriesId = series.Id,
            OccurrenceIndex = i,
            RowVersion = []
        }).ToList();
        db.Events.AddRange(occurrences);
        await db.SaveChangesAsync();
        return (series.Id, occurrences[0].Id, occurrences[1].Id);
    }

    private async Task<Guid> SeedTeamAsync(Guid ownerId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = $"Regulars {Guid.NewGuid():N}",
            MaxMembers = 10,
            OwnerId = ownerId,
            RowVersion = []
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync();
        return team.Id;
    }

    private async Task SeedPlayerAsync(Guid playerId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        db.Players.Add(new Player { Id = playerId, Username = $"unlinked-{Guid.NewGuid():N}", RowVersion = [] });
        await db.SaveChangesAsync();
    }

    private async Task MutatePlayerAsync(Guid playerId, Action<Player> mutate)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        mutate(await db.Players.SingleAsync(p => p.Id == playerId));
        await db.SaveChangesAsync();
    }

    private async Task MutateEventAsync(Guid occurrenceId, Action<EventOccurrence> mutate)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        mutate(await db.Events.SingleAsync(e => e.Id == occurrenceId));
        await db.SaveChangesAsync();
    }

    private async Task<EventOccurrence> EventAsync(Guid occurrenceId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        return await db.Events.AsNoTracking().SingleAsync(e => e.Id == occurrenceId);
    }

    /// <summary>Seeds a row directly, for statuses no endpoint creates (Reserved, CheckedIn, Active, history).</summary>
    private async Task<RosterAssignment> SeedAssignmentAsync(Guid occurrenceId, Guid playerId, RosterAssignmentStatus status, Guid requirementId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var assignment = new RosterAssignment
        {
            Id = Guid.NewGuid(),
            OccurrenceId = occurrenceId,
            PlayerId = playerId,
            RequirementId = requirementId,
            RoleCode = "participant",
            Status = status,
            Source = RosterAssignmentSource.Host,
            ConfirmedAtUtc = DateTime.UtcNow.AddHours(-1),
            RowVersion = []
        };
        db.RosterAssignments.Add(assignment);
        await db.SaveChangesAsync();
        return assignment;
    }

    private async Task<RosterAssignment> AssignmentAsync(Guid assignmentId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        return await db.RosterAssignments.AsNoTracking().SingleAsync(a => a.Id == assignmentId);
    }

    private async Task<List<RosterAssignment>> AssignmentsOfAsync(Guid occurrenceId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        return await db.RosterAssignments.AsNoTracking().Where(a => a.OccurrenceId == occurrenceId).ToListAsync();
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
