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
/// Player self-claim (<c>POST …/roster/claims</c>), self-leave
/// (<c>POST …/roster/assignments/{id}/leave</c>) and host removal
/// (<c>POST …/roster/assignments/{id}/remove</c>): authorization order, status policy,
/// transitions, history retention, replacement lineage and privacy. Concurrency is covered on
/// real SQL Server by <see cref="RosterSelfClaimSqlServerIntegrationTests"/>.
/// </summary>
public sealed class RosterSelfServiceControllerIntegrationTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private readonly TeamBuilderWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public RosterSelfServiceControllerIntegrationTests(TeamBuilderWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── claim: authorization and lookup order ────────────────────────────────

    [Fact]
    public async Task Claim_WithoutToken_Returns401()
    {
        var (_, occurrence, requirementId) = await SeedPickupAsync();

        var response = await _client.PostAsJsonAsync(ClaimsUrl(occurrence.Id), new { requirementId });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Claim_UnlinkedIdentity_Returns403_AndCreatesNothing()
    {
        var (_, occurrence, requirementId) = await SeedPickupAsync();

        var response = await SendAsync(HttpMethod.Post, ClaimsUrl(occurrence.Id), LinkedPlayerTokens.ForSubject(LinkedPlayerTokens.NewSubject()), new { requirementId });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AssignmentsOfAsync(occurrence.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Claim_MissingOccurrence_Returns404()
    {
        var (_, _, requirementId) = await SeedPickupAsync();

        var response = await ClaimAsync(await NewPlayerTokenAsync(), Guid.NewGuid(), new { requirementId });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Claim_MissingRequirement_Returns404()
    {
        var (_, occurrence, _) = await SeedPickupAsync();

        var response = await ClaimAsync(await NewPlayerTokenAsync(), occurrence.Id, new { requirementId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Claim_RequirementOfAnotherOccurrence_Returns404_AndTouchesNeither()
    {
        var (_, occurrence, _) = await SeedPickupAsync();
        var (_, other, otherRequirementId) = await SeedPickupAsync();

        var response = await ClaimAsync(await NewPlayerTokenAsync(), occurrence.Id, new { requirementId = otherRequirementId });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AssignmentsOfAsync(occurrence.Id)).Should().BeEmpty();
        (await AssignmentsOfAsync(other.Id)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"requirementId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"requirementId\":\"6f2c4e0a-3d4b-4b7e-9a51-0c6a3f7d1e22\",\"replacesAssignmentId\":\"00000000-0000-0000-0000-000000000000\"}")]
    public async Task Claim_InvalidBody_Returns400(string json)
    {
        var (_, occurrence, _) = await SeedPickupAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, ClaimsUrl(occurrence.Id))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await NewPlayerTokenAsync());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(EventStatus.Completed)]
    [InlineData(EventStatus.Cancelled)]
    [InlineData(EventStatus.Archived)]
    public async Task Claim_ClosedOccurrence_Returns409OccurrenceClosed(EventStatus status)
    {
        var (_, occurrence, requirementId) = await SeedPickupAsync();
        await SetStatusAsync(occurrence.Id, status);

        var response = await ClaimAsync(await NewPlayerTokenAsync(), occurrence.Id, new { requirementId });

        await ExpectConflictAsync(response, RosterConflictCodes.OccurrenceClosed);
        (await AssignmentsOfAsync(occurrence.Id)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(EventStatus.Planned)]
    [InlineData(EventStatus.Open)]
    [InlineData(EventStatus.InProgress)]
    public async Task Claim_JoinableOccurrence_Returns201ConfirmedPlayerAssignment(EventStatus status)
    {
        var (_, occurrence, requirementId) = await SeedPickupAsync(status);
        var playerId = Guid.NewGuid();
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, playerId);

        var response = await ClaimAsync(token, occurrence.Id, new { requirementId });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.AbsolutePath.Should().Be($"/api/v1/events/{occurrence.Id}/roster/assignments");
        var assignment = (await response.Content.ReadFromJsonAsync<RosterAssignmentDto>())!;
        assignment.PlayerId.Should().Be(playerId);
        assignment.OccurrenceId.Should().Be(occurrence.Id);
        assignment.RequirementId.Should().Be(requirementId);
        assignment.RoleCode.Should().Be("participant");
        assignment.Status.Should().Be(RosterAssignmentStatus.Confirmed);
        assignment.Source.Should().Be(RosterAssignmentSource.Player);
        assignment.ConfirmedAtUtc.Should().NotBeNull();
        assignment.ConfirmedAtUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
        assignment.ReservedAtUtc.Should().BeNull();
        assignment.DepartedAtUtc.Should().BeNull();
        assignment.ExitReason.Should().BeNull();
    }

    [Fact]
    public async Task Claim_OrphanedOccurrence_IsStillAllowed()
    {
        // Self-claim needs no host action, so an occurrence without a host still accepts claims.
        var (_, occurrence, requirementId) = await SeedPickupAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
            (await db.Events.SingleAsync(e => e.Id == occurrence.Id)).HostId = null;
            await db.SaveChangesAsync();
        }

        (await ClaimAsync(await NewPlayerTokenAsync(), occurrence.Id, new { requirementId })).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ── claim: host participation and team independence ──────────────────────

    [Fact]
    public async Task Host_HoldsNoSpotUntilTheyClaim_ThroughTheSameEndpoint()
    {
        var (hostToken, occurrence, requirementId) = await SeedPickupAsync();

        await ExpectSummaryAsync(occurrence.Id, supply: 0, open: 10, ready: false);

        var response = await ClaimAsync(hostToken, occurrence.Id, new { requirementId });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var assignment = (await response.Content.ReadFromJsonAsync<RosterAssignmentDto>())!;
        assignment.PlayerId.Should().Be(occurrence.HostId!.Value);
        assignment.Source.Should().Be(RosterAssignmentSource.Player);
        await ExpectSummaryAsync(occurrence.Id, supply: 1, open: 9, ready: false);
    }

    [Fact]
    public async Task Claim_TeamEvent_MemberAndNonMemberShareTheSameRoster()
    {
        var hostId = Guid.NewGuid();
        var hostToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, hostId);
        var memberId = Guid.NewGuid();
        var memberToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, memberId);
        var teamId = await SeedTeamWithMemberAsync(hostId, memberId);
        var occurrence = await SeedEventAsync(hostId, EventStatus.Planned, teamId);
        var requirementId = await CreateRequirementAsync(hostToken, occurrence.Id, 10);

        (await ClaimAsync(memberToken, occurrence.Id, new { requirementId })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await ClaimAsync(await NewPlayerTokenAsync(), occurrence.Id, new { requirementId })).StatusCode.Should().Be(HttpStatusCode.Created);

        await ExpectSummaryAsync(occurrence.Id, supply: 2, open: 8, ready: false);
    }

    // ── claim: duplicates, capacity, replacement ─────────────────────────────

    [Fact]
    public async Task Claim_Retried_ReturnsTheSameAssignmentWith200()
    {
        var (_, occurrence, requirementId) = await SeedPickupAsync();
        var token = await NewPlayerTokenAsync();

        var first = await ClaimAsync(token, occurrence.Id, new { requirementId });
        var retry = await ClaimAsync(token, occurrence.Id, new { requirementId });

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        (await retry.Content.ReadFromJsonAsync<RosterAssignmentDto>())!.Id
            .Should().Be((await first.Content.ReadFromJsonAsync<RosterAssignmentDto>())!.Id);
        (await AssignmentsOfAsync(occurrence.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Claim_RetriedAfterTheRequirementFilled_StillReturnsTheHeldAssignment()
    {
        var (_, occurrence, requirementId) = await SeedPickupAsync(requiredCount: 1);
        var token = await NewPlayerTokenAsync();
        (await ClaimAsync(token, occurrence.Id, new { requirementId })).StatusCode.Should().Be(HttpStatusCode.Created);

        (await ClaimAsync(token, occurrence.Id, new { requirementId })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Claim_HostAssignedPlayerClaimsTheSameRequirement_Returns200WithTheHostAssignment()
    {
        var (hostToken, occurrence, requirementId) = await SeedPickupAsync();
        var playerId = Guid.NewGuid();
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, playerId);
        var hostAssigned = await SendAsync(HttpMethod.Post, $"{RosterUrl(occurrence.Id)}/assignments", hostToken, new { playerId, requirementId });
        hostAssigned.StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await ClaimAsync(token, occurrence.Id, new { requirementId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<RosterAssignmentDto>())!.Source.Should().Be(RosterAssignmentSource.Host);
        (await AssignmentsOfAsync(occurrence.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Claim_WhileHoldingAnotherRequirement_Returns409AlreadyParticipating()
    {
        var (hostToken, occurrence, guardId) = await SeedPickupAsync(roleCode: "guard", requiredCount: 2);
        var forwardId = await CreateRequirementAsync(hostToken, occurrence.Id, 2, "forward");
        var token = await NewPlayerTokenAsync();
        (await ClaimAsync(token, occurrence.Id, new { requirementId = guardId })).StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await ClaimAsync(token, occurrence.Id, new { requirementId = forwardId });

        var body = await ExpectConflictAsync(response, RosterConflictCodes.AlreadyParticipating);
        body.Should().NotContainAny("UX_", "IX_", "duplicate key", "SqlException");
        (await AssignmentsOfAsync(occurrence.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Claim_FullRequirement_Returns409RequirementFull()
    {
        var (_, occurrence, requirementId) = await SeedPickupAsync(requiredCount: 2);
        for (var i = 0; i < 2; i++)
            (await ClaimAsync(await NewPlayerTokenAsync(), occurrence.Id, new { requirementId })).StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await ClaimAsync(await NewPlayerTokenAsync(), occurrence.Id, new { requirementId });

        await ExpectConflictAsync(response, RosterConflictCodes.RequirementFull);
        await ExpectSummaryAsync(occurrence.Id, supply: 2, open: 0, ready: true, required: 2);
    }

    [Fact]
    public async Task Claim_AfterLeaving_MayRejoinAsANewRow()
    {
        var (_, occurrence, requirementId) = await SeedPickupAsync();
        var token = await NewPlayerTokenAsync();
        var first = await ClaimedAsync(token, occurrence.Id, requirementId);
        (await LeaveAsync(token, occurrence.Id, first.Id)).StatusCode.Should().Be(HttpStatusCode.OK);

        var again = await ClaimAsync(token, occurrence.Id, new { requirementId });

        again.StatusCode.Should().Be(HttpStatusCode.Created);
        (await again.Content.ReadFromJsonAsync<RosterAssignmentDto>())!.Id.Should().NotBe(first.Id);
        (await AssignmentsOfAsync(occurrence.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Claim_WithReplacesAssignmentId_RecordsLineage()
    {
        var (_, occurrence, requirementId) = await SeedPickupAsync();
        var leaverToken = await NewPlayerTokenAsync();
        var original = await ClaimedAsync(leaverToken, occurrence.Id, requirementId);
        (await LeaveAsync(leaverToken, occurrence.Id, original.Id)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await ClaimAsync(await NewPlayerTokenAsync(), occurrence.Id, new { requirementId, replacesAssignmentId = original.Id });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await response.Content.ReadFromJsonAsync<RosterAssignmentDto>())!.ReplacedAssignmentId.Should().Be(original.Id);
        var stored = (await AssignmentsOfAsync(occurrence.Id)).Single(a => a.Id == original.Id);
        (stored.Status, stored.ExitReason).Should().Be((RosterAssignmentStatus.Cancelled, RosterExitReason.PlayerLeft));
    }

    [Fact]
    public async Task Claim_ReplacingALiveAssignment_Returns409()
    {
        var (_, occurrence, requirementId) = await SeedPickupAsync();
        var live = await ClaimedAsync(await NewPlayerTokenAsync(), occurrence.Id, requirementId);

        var response = await ClaimAsync(await NewPlayerTokenAsync(), occurrence.Id, new { requirementId, replacesAssignmentId = live.Id });

        await ExpectConflictAsync(response, RosterConflictCodes.ReplacedAssignmentStillActive);
    }

    [Fact]
    public async Task Claim_ReplacingAnAssignmentOfAnotherRequirementOrOccurrence_Returns400()
    {
        var (hostToken, occurrence, guardId) = await SeedPickupAsync(roleCode: "guard", requiredCount: 2);
        var forwardId = await CreateRequirementAsync(hostToken, occurrence.Id, 2, "forward");
        var guardLeaverToken = await NewPlayerTokenAsync();
        var guard = await ClaimedAsync(guardLeaverToken, occurrence.Id, guardId);
        await LeaveAsync(guardLeaverToken, occurrence.Id, guard.Id);
        var (_, other, otherRequirementId) = await SeedPickupAsync();
        var otherLeaverToken = await NewPlayerTokenAsync();
        var foreign = await ClaimedAsync(otherLeaverToken, other.Id, otherRequirementId);
        await LeaveAsync(otherLeaverToken, other.Id, foreign.Id);
        var token = await NewPlayerTokenAsync();

        (await ClaimAsync(token, occurrence.Id, new { requirementId = forwardId, replacesAssignmentId = guard.Id }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ClaimAsync(token, occurrence.Id, new { requirementId = forwardId, replacesAssignmentId = foreign.Id }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ClaimAsync(token, occurrence.Id, new { requirementId = forwardId, replacesAssignmentId = Guid.NewGuid() }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AssignmentsOfAsync(occurrence.Id)).Should().ContainSingle();
    }

    // ── leave ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Leave_WithoutToken_Returns401()
    {
        var (_, occurrence, requirementId) = await SeedPickupAsync();
        var held = await ClaimedAsync(await NewPlayerTokenAsync(), occurrence.Id, requirementId);

        (await _client.PostAsync(LeaveUrl(occurrence.Id, held.Id), null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Leave_UnlinkedIdentity_Returns403()
    {
        var (_, occurrence, requirementId) = await SeedPickupAsync();
        var held = await ClaimedAsync(await NewPlayerTokenAsync(), occurrence.Id, requirementId);

        var response = await LeaveAsync(LinkedPlayerTokens.ForSubject(LinkedPlayerTokens.NewSubject()), occurrence.Id, held.Id);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Leave_MissingOccurrenceOrAssignment_Returns404()
    {
        var (_, occurrence, requirementId) = await SeedPickupAsync();
        var token = await NewPlayerTokenAsync();
        var held = await ClaimedAsync(token, occurrence.Id, requirementId);
        var (_, other, _) = await SeedPickupAsync();

        (await LeaveAsync(token, Guid.NewGuid(), held.Id)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LeaveAsync(token, occurrence.Id, Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LeaveAsync(token, other.Id, held.Id)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AssignmentsOfAsync(occurrence.Id)).Single().Status.Should().Be(RosterAssignmentStatus.Confirmed);
    }

    [Fact]
    public async Task Leave_SomeoneElsesAssignment_Returns403_EvenForTheHost()
    {
        var (hostToken, occurrence, requirementId) = await SeedPickupAsync();
        var held = await ClaimedAsync(await NewPlayerTokenAsync(), occurrence.Id, requirementId);

        (await LeaveAsync(await NewPlayerTokenAsync(), occurrence.Id, held.Id)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await LeaveAsync(hostToken, occurrence.Id, held.Id)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AssignmentsOfAsync(occurrence.Id)).Single().Status.Should().Be(RosterAssignmentStatus.Confirmed);
    }

    [Theory]
    [InlineData(RosterAssignmentStatus.Reserved, RosterAssignmentStatus.Cancelled)]
    [InlineData(RosterAssignmentStatus.Confirmed, RosterAssignmentStatus.Cancelled)]
    [InlineData(RosterAssignmentStatus.CheckedIn, RosterAssignmentStatus.Departed)]
    [InlineData(RosterAssignmentStatus.Active, RosterAssignmentStatus.Departed)]
    public async Task Leave_EndsTheAssignment_KeepsTheRow_AndReopensTheSpot(RosterAssignmentStatus from, RosterAssignmentStatus to)
    {
        var (_, occurrence, requirementId) = await SeedPickupAsync(requiredCount: 1);
        var playerId = Guid.NewGuid();
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, playerId);
        var held = await SeedAssignmentAsync(occurrence.Id, playerId, from, requirementId);
        await ExpectSummaryAsync(occurrence.Id, supply: 1, open: 0, ready: true, required: 1);

        var response = await LeaveAsync(token, occurrence.Id, held.Id);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var ended = (await response.Content.ReadFromJsonAsync<RosterAssignmentDto>())!;
        ended.Id.Should().Be(held.Id);
        ended.Status.Should().Be(to);
        ended.ExitReason.Should().Be(RosterExitReason.PlayerLeft);
        ended.DepartedAtUtc.Should().NotBeNull();
        ended.DepartedAtUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
        ended.PlayerId.Should().Be(playerId);
        await ExpectSummaryAsync(occurrence.Id, supply: 0, open: 1, ready: false, required: 1);
        (await AssignmentsOfAsync(occurrence.Id)).Should().ContainSingle(a => a.Id == held.Id && a.Status == to);
    }

    [Fact]
    public async Task Leave_Twice_Returns409AssignmentEnded()
    {
        var (_, occurrence, requirementId) = await SeedPickupAsync();
        var token = await NewPlayerTokenAsync();
        var held = await ClaimedAsync(token, occurrence.Id, requirementId);
        (await LeaveAsync(token, occurrence.Id, held.Id)).StatusCode.Should().Be(HttpStatusCode.OK);

        await ExpectConflictAsync(await LeaveAsync(token, occurrence.Id, held.Id), RosterConflictCodes.AssignmentEnded);
    }

    [Theory]
    [InlineData(RosterAssignmentStatus.Departed)]
    [InlineData(RosterAssignmentStatus.NoShow)]
    [InlineData(RosterAssignmentStatus.Cancelled)]
    public async Task Leave_HistoricalAssignment_Returns409AndChangesNothing(RosterAssignmentStatus status)
    {
        var (_, occurrence, requirementId) = await SeedPickupAsync();
        var playerId = Guid.NewGuid();
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, playerId);
        var held = await SeedAssignmentAsync(occurrence.Id, playerId, status, requirementId);

        await ExpectConflictAsync(await LeaveAsync(token, occurrence.Id, held.Id), RosterConflictCodes.AssignmentEnded);
        (await AssignmentsOfAsync(occurrence.Id)).Single().Status.Should().Be(status);
    }

    [Fact]
    public async Task Leave_ClosedOccurrence_Returns409OccurrenceClosed()
    {
        var (_, occurrence, requirementId) = await SeedPickupAsync();
        var token = await NewPlayerTokenAsync();
        var held = await ClaimedAsync(token, occurrence.Id, requirementId);
        await SetStatusAsync(occurrence.Id, EventStatus.Completed);

        await ExpectConflictAsync(await LeaveAsync(token, occurrence.Id, held.Id), RosterConflictCodes.OccurrenceClosed);
        (await AssignmentsOfAsync(occurrence.Id)).Single().Status.Should().Be(RosterAssignmentStatus.Confirmed);
    }

    // ── host remove ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Remove_AuthorizationOrder()
    {
        var (hostToken, occurrence, requirementId) = await SeedPickupAsync();
        var playerToken = await NewPlayerTokenAsync();
        var held = await ClaimedAsync(playerToken, occurrence.Id, requirementId);

        (await _client.PostAsync(RemoveUrl(occurrence.Id, held.Id), null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await RemoveAsync(LinkedPlayerTokens.ForSubject(LinkedPlayerTokens.NewSubject()), occurrence.Id, held.Id)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await RemoveAsync(hostToken, Guid.NewGuid(), held.Id)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await RemoveAsync(playerToken, occurrence.Id, held.Id)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await RemoveAsync(hostToken, occurrence.Id, Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AssignmentsOfAsync(occurrence.Id)).Single().Status.Should().Be(RosterAssignmentStatus.Confirmed);
    }

    [Fact]
    public async Task Remove_OrphanedOccurrence_Returns409()
    {
        var (hostToken, occurrence, requirementId) = await SeedPickupAsync();
        var held = await ClaimedAsync(await NewPlayerTokenAsync(), occurrence.Id, requirementId);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
            (await db.Events.SingleAsync(e => e.Id == occurrence.Id)).HostId = null;
            await db.SaveChangesAsync();
        }

        (await RemoveAsync(hostToken, occurrence.Id, held.Id)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData(RosterAssignmentStatus.Confirmed, RosterAssignmentStatus.Cancelled)]
    [InlineData(RosterAssignmentStatus.Active, RosterAssignmentStatus.Departed)]
    public async Task Remove_ByHost_EndsWithHostRemoved_AndKeepsHistory(RosterAssignmentStatus from, RosterAssignmentStatus to)
    {
        var (hostToken, occurrence, requirementId) = await SeedPickupAsync(EventStatus.InProgress);
        var playerId = Guid.NewGuid();
        await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, playerId);
        var held = await SeedAssignmentAsync(occurrence.Id, playerId, from, requirementId);

        var response = await RemoveAsync(hostToken, occurrence.Id, held.Id);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var ended = (await response.Content.ReadFromJsonAsync<RosterAssignmentDto>())!;
        (ended.Status, ended.ExitReason).Should().Be((to, RosterExitReason.HostRemoved));
        ended.DepartedAtUtc.Should().NotBeNull();
        await ExpectSummaryAsync(occurrence.Id, supply: 0, open: 10, ready: false);
        (await AssignmentsOfAsync(occurrence.Id)).Should().ContainSingle(a => a.Id == held.Id);
        await ExpectConflictAsync(await RemoveAsync(hostToken, occurrence.Id, held.Id), RosterConflictCodes.AssignmentEnded);
    }

    [Fact]
    public async Task Remove_HostMayRemoveTheirOwnClaim()
    {
        var (hostToken, occurrence, requirementId) = await SeedPickupAsync();
        var held = await ClaimedAsync(hostToken, occurrence.Id, requirementId);

        (await RemoveAsync(hostToken, occurrence.Id, held.Id)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Remove_ClosedOccurrence_Returns409()
    {
        var (hostToken, occurrence, requirementId) = await SeedPickupAsync();
        var held = await ClaimedAsync(await NewPlayerTokenAsync(), occurrence.Id, requirementId);
        await SetStatusAsync(occurrence.Id, EventStatus.Cancelled);

        (await RemoveAsync(hostToken, occurrence.Id, held.Id)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await AssignmentsOfAsync(occurrence.Id)).Single().Status.Should().Be(RosterAssignmentStatus.Confirmed);
    }

    // ── refill and privacy ───────────────────────────────────────────────────

    [Fact]
    public async Task InProgress_DepartureThenReplacementClaim_ReturnsToReady()
    {
        var (_, occurrence, requirementId) = await SeedPickupAsync(requiredCount: 2);
        var leaverId = Guid.NewGuid();
        var leaverToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, leaverId);
        var leaver = await SeedAssignmentAsync(occurrence.Id, leaverId, RosterAssignmentStatus.Active, requirementId);
        var stayerId = Guid.NewGuid();
        await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, stayerId);
        await SeedAssignmentAsync(occurrence.Id, stayerId, RosterAssignmentStatus.Active, requirementId);
        await SetStatusAsync(occurrence.Id, EventStatus.InProgress);

        (await LeaveAsync(leaverToken, occurrence.Id, leaver.Id)).StatusCode.Should().Be(HttpStatusCode.OK);
        await ExpectSummaryAsync(occurrence.Id, supply: 1, open: 1, ready: false, required: 2);

        (await ClaimAsync(await NewPlayerTokenAsync(), occurrence.Id, new { requirementId, replacesAssignmentId = leaver.Id }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        await ExpectSummaryAsync(occurrence.Id, supply: 2, open: 0, ready: true, required: 2);
        (await AssignmentsOfAsync(occurrence.Id)).Single(a => a.Id == leaver.Id).Status.Should().Be(RosterAssignmentStatus.Departed);
    }

    [Fact]
    public async Task ClaimLeaveAndRemoveResponses_ExposeNoPrivateIdentityData()
    {
        var (hostToken, occurrence, requirementId) = await SeedPickupAsync();
        var playerId = Guid.NewGuid();
        await SeedPlayerWithEmailAsync(playerId, "secret-claimer@example.com");
        var token = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, playerId);

        var claim = await ClaimAsync(token, occurrence.Id, new { requirementId });
        var assignmentId = (await claim.Content.ReadFromJsonAsync<RosterAssignmentDto>())!.Id;
        var retry = await ClaimAsync(token, occurrence.Id, new { requirementId });
        var leave = await LeaveAsync(token, occurrence.Id, assignmentId);
        var second = await ClaimedAsync(token, occurrence.Id, requirementId);
        var remove = await RemoveAsync(hostToken, occurrence.Id, second.Id);
        var summary = await _client.GetAsync(RosterUrl(occurrence.Id));

        foreach (var response in new[] { claim, retry, leave, remove, summary })
        {
            var json = await response.Content.ReadAsStringAsync();
            json.Should().NotContainAny("secret-claimer", "email", "Email", "ext|", "issuer", "subject", "tenant", "provider", "teambuilder-test");
        }

        using var document = JsonDocument.Parse(await claim.Content.ReadAsStringAsync());
        document.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "occurrenceId", "playerId", "username", "displayName", "requirementId", "roleCode", "sourceRoleLabel",
            "status", "source", "reservedAtUtc", "confirmedAtUtc", "checkedInAtUtc", "activatedAtUtc", "departedAtUtc",
            "exitReason", "replacedAssignmentId", "createdAtUtc", "updatedAtUtc");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string RosterUrl(Guid occurrenceId) => $"/api/v1/events/{occurrenceId}/roster";
    private static string ClaimsUrl(Guid occurrenceId) => $"{RosterUrl(occurrenceId)}/claims";
    private static string LeaveUrl(Guid occurrenceId, Guid assignmentId) => $"{RosterUrl(occurrenceId)}/assignments/{assignmentId}/leave";
    private static string RemoveUrl(Guid occurrenceId, Guid assignmentId) => $"{RosterUrl(occurrenceId)}/assignments/{assignmentId}/remove";

    private Task<HttpResponseMessage> ClaimAsync(string token, Guid occurrenceId, object body) =>
        SendAsync(HttpMethod.Post, ClaimsUrl(occurrenceId), token, body);

    private Task<HttpResponseMessage> LeaveAsync(string token, Guid occurrenceId, Guid assignmentId) =>
        SendAsync(HttpMethod.Post, LeaveUrl(occurrenceId, assignmentId), token);

    private Task<HttpResponseMessage> RemoveAsync(string token, Guid occurrenceId, Guid assignmentId) =>
        SendAsync(HttpMethod.Post, RemoveUrl(occurrenceId, assignmentId), token);

    private async Task<RosterAssignmentDto> ClaimedAsync(string token, Guid occurrenceId, Guid requirementId)
    {
        var response = await ClaimAsync(token, occurrenceId, new { requirementId });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RosterAssignmentDto>())!;
    }

    private static async Task<string> ExpectConflictAsync(HttpResponseMessage response, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("code").GetString().Should().Be(code);
        return body;
    }

    private async Task ExpectSummaryAsync(Guid occurrenceId, int supply, int open, bool ready, int required = 10)
    {
        var summary = (await _client.GetFromJsonAsync<RosterSummaryDto>(RosterUrl(occurrenceId)))!;
        (summary.RequiredCount, summary.SupplyCount, summary.OpenQuantity, summary.IsRosterReady)
            .Should().Be((required, supply, open, ready));
    }

    private Task<string> NewPlayerTokenAsync() => LinkedPlayerTokens.ForPlayerAsync(_factory.Services, Guid.NewGuid());

    private async Task<(string HostToken, EventOccurrence Occurrence, Guid RequirementId)> SeedPickupAsync(
        EventStatus status = EventStatus.Planned,
        int requiredCount = 10,
        string roleCode = "participant")
    {
        var hostId = Guid.NewGuid();
        var hostToken = await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, hostId);
        var occurrence = await SeedEventAsync(hostId, EventStatus.Planned);
        var requirementId = await CreateRequirementAsync(hostToken, occurrence.Id, requiredCount, roleCode);
        if (status != EventStatus.Planned)
            await SetStatusAsync(occurrence.Id, status);
        return (hostToken, occurrence, requirementId);
    }

    private async Task<Guid> CreateRequirementAsync(string hostToken, Guid occurrenceId, int requiredCount, string roleCode = "participant")
    {
        var response = await SendAsync(HttpMethod.Post, $"{RosterUrl(occurrenceId)}/requirements", hostToken, new { roleCode, requiredCount });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RosterRequirementDto>())!.Id;
    }

    private async Task<EventOccurrence> SeedEventAsync(Guid hostId, EventStatus status, Guid? teamId = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var occurrence = new EventOccurrence
        {
            Id = Guid.NewGuid(),
            Name = "Wednesday 8 PM pickup",
            ScheduledStartUtc = DateTime.UtcNow.AddDays(2),
            Status = status,
            MaxParticipants = 10,
            HostId = hostId,
            TeamId = teamId,
            RowVersion = []
        };
        db.Events.Add(occurrence);
        await db.SaveChangesAsync();
        return occurrence;
    }

    private async Task SetStatusAsync(Guid occurrenceId, EventStatus status)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        (await db.Events.SingleAsync(e => e.Id == occurrenceId)).Status = status;
        await db.SaveChangesAsync();
    }

    private async Task SeedPlayerWithEmailAsync(Guid playerId, string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        db.Players.Add(new Player { Id = playerId, Username = $"claimer-{Guid.NewGuid():N}", Email = email, RowVersion = [] });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedTeamWithMemberAsync(Guid ownerId, Guid memberId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Wednesday regulars",
            MaxMembers = 10,
            CurrentMemberCount = 1,
            OwnerId = ownerId,
            RowVersion = []
        };
        db.Teams.Add(team);
        db.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            PlayerId = memberId,
            Role = TeamRole.Member,
            JoinedAtUtc = DateTime.UtcNow,
            IsActive = true,
            RowVersion = []
        });
        await db.SaveChangesAsync();
        return team.Id;
    }

    /// <summary>Seeds a row directly, for statuses no endpoint creates yet (CheckedIn, Active, history).</summary>
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
