using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// /api/v1/events/{occurrenceId}/roster: requirements and host assignments over HTTP. Event
/// participation is independent of team membership.
/// </summary>
public sealed class EventRosterControllerIntegrationTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private readonly TeamBuilderWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public EventRosterControllerIntegrationTests(TeamBuilderWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── requirements ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateRequirement_GenericParticipant_DefaultsRoleCodeToParticipant()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();

        var response = await PostRequirementAsync(hostId, occurrence.Id, new { requiredCount = 10 });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.AbsolutePath.Should().Be(RequirementsUrl(occurrence.Id));
        var requirement = (await response.Content.ReadFromJsonAsync<RosterRequirementDto>())!;
        requirement.OccurrenceId.Should().Be(occurrence.Id);
        requirement.RoleCode.Should().Be("participant");
        requirement.RequiredCount.Should().Be(10);
        requirement.SupplyCount.Should().Be(0);
        requirement.OpenQuantity.Should().Be(10);
    }

    [Fact]
    public async Task CreateRequirement_ExplicitParticipant_IsTheSameGenericRole()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        (await PostRequirementAsync(hostId, occurrence.Id, new { requiredCount = 4 })).StatusCode.Should().Be(HttpStatusCode.Created);

        var duplicate = await PostRequirementAsync(hostId, occurrence.Id, new { roleCode = "Participant", requiredCount = 4 });

        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateRequirement_BasketballRoles_OneOccurrenceHasMultipleRequirements()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();

        var guard = await CreateRequirementAsync(hostId, occurrence.Id, new { roleCode = "guard", displayPosition = "Guard", sourceRoleLabel = "Point Guard", requiredCount = 2 });
        await CreateRequirementAsync(hostId, occurrence.Id, new { roleCode = "forward", requiredCount = 2 });
        await CreateRequirementAsync(hostId, occurrence.Id, new { roleCode = "center", requiredCount = 1 });

        guard.RoleCode.Should().Be("guard");
        guard.DisplayPosition.Should().Be("Guard");
        guard.SourceRoleLabel.Should().Be("Point Guard");

        var requirements = await GetRequirementsAsync(occurrence.Id);
        requirements.Select(r => (r.RoleCode, r.RequiredCount, r.OpenQuantity))
            .Should().Equal(("guard", 2, 2), ("forward", 2, 2), ("center", 1, 1));
    }

    [Fact]
    public async Task CreateRequirement_MmorpgRoles_NormalizeCase()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();

        await CreateRequirementAsync(hostId, occurrence.Id, new { roleCode = "tank", requiredCount = 2 });
        await CreateRequirementAsync(hostId, occurrence.Id, new { roleCode = "Healer", sourceRoleLabel = "Heals", requiredCount = 4 });
        await CreateRequirementAsync(hostId, occurrence.Id, new { roleCode = "damage", sourceRoleLabel = "DPS", requiredCount = 14 });

        (await GetRequirementsAsync(occurrence.Id)).Select(r => r.RoleCode).Should().Equal("tank", "healer", "damage");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100001)]
    public async Task CreateRequirement_RequiredCountOutOfRange_Returns400(int requiredCount)
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();

        var response = await PostRequirementAsync(hostId, occurrence.Id, new { roleCode = "guard", requiredCount });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetRequirementsAsync(occurrence.Id)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100000)]
    public async Task CreateRequirement_RequiredCountBoundaries_Accepted(int requiredCount)
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();

        var requirement = await CreateRequirementAsync(hostId, occurrence.Id, new { requiredCount });

        requirement.RequiredCount.Should().Be(requiredCount);
    }

    [Fact]
    public async Task CreateRequirement_MissingRequiredCount_Returns400()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();

        (await PostRequirementAsync(hostId, occurrence.Id, new { roleCode = "guard" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("point guard")]
    [InlineData("-tank")]
    [InlineData("")]
    public async Task CreateRequirement_MalformedRoleCode_Returns400(string roleCode)
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();

        (await PostRequirementAsync(hostId, occurrence.Id, new { roleCode, requiredCount = 1 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateRequirement_DuplicateRole_Returns409()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        await CreateRequirementAsync(hostId, occurrence.Id, new { roleCode = "guard", requiredCount = 2 });

        var response = await PostRequirementAsync(hostId, occurrence.Id, new { roleCode = " GUARD ", requiredCount = 3 });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await GetRequirementsAsync(occurrence.Id)).Should().ContainSingle().Which.RequiredCount.Should().Be(2);
    }

    [Fact]
    public async Task Requirements_AreIndependentBetweenOccurrences()
    {
        var (hostId, first) = await SeedHostedEventAsync();
        var second = await SeedEventAsync(hostId);

        await CreateRequirementAsync(hostId, first.Id, new { roleCode = "guard", requiredCount = 2 });
        await CreateRequirementAsync(hostId, second.Id, new { roleCode = "guard", requiredCount = 3 });
        await AssignAsync(hostId, first.Id, new { playerId = await SeedPlayerAsync(), requirementId = (await GetRequirementsAsync(first.Id))[0].Id });

        (await GetRequirementsAsync(first.Id)).Should().ContainSingle().Which.Should().Match<RosterRequirementDto>(r => r.RequiredCount == 2 && r.SupplyCount == 1);
        (await GetRequirementsAsync(second.Id)).Should().ContainSingle().Which.Should().Match<RosterRequirementDto>(r => r.RequiredCount == 3 && r.SupplyCount == 0);
    }

    [Fact]
    public async Task CreateRequirement_NonHost_Returns403()
    {
        var (_, occurrence) = await SeedHostedEventAsync();

        var response = await PostRequirementAsync(Guid.NewGuid(), occurrence.Id, new { requiredCount = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetRequirementsAsync(occurrence.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateRequirement_OrphanedOccurrence_Returns409()
    {
        var occurrence = await SeedEventAsync(hostId: null);

        var response = await PostRequirementAsync(Guid.NewGuid(), occurrence.Id, new { requiredCount = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateRequirement_MissingOccurrence_Returns404()
    {
        (await PostRequirementAsync(Guid.NewGuid(), Guid.NewGuid(), new { requiredCount = 1 })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateRequirement_Unauthenticated_Returns401()
    {
        var (_, occurrence) = await SeedHostedEventAsync();

        (await _client.PostAsJsonAsync(RequirementsUrl(occurrence.Id), new { requiredCount = 1 })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateRequirement_UnlinkedIdentity_Returns403()
    {
        var (_, occurrence) = await SeedHostedEventAsync();

        var response = await SendAsync(HttpMethod.Post, RequirementsUrl(occurrence.Id), LinkedPlayerTokens.ForSubject(LinkedPlayerTokens.NewSubject()), new { requiredCount = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(EventStatus.Completed)]
    [InlineData(EventStatus.Cancelled)]
    [InlineData(EventStatus.Archived)]
    public async Task RosterMutation_ClosedOccurrence_Returns409(EventStatus status)
    {
        var (hostId, occurrence) = await SeedHostedEventAsync(status);

        (await PostRequirementAsync(hostId, occurrence.Id, new { requiredCount = 1 })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PostAssignmentAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync() })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await GetRequirementsAsync(occurrence.Id)).Should().BeEmpty();
        (await GetAssignmentsAsync(occurrence.Id)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(EventStatus.Planned)]
    [InlineData(EventStatus.Open)]
    [InlineData(EventStatus.InProgress)]
    public async Task RosterMutation_OpenOrInProgressOccurrence_IsAllowed(EventStatus status)
    {
        var (hostId, occurrence) = await SeedHostedEventAsync(status);

        (await PostRequirementAsync(hostId, occurrence.Id, new { requiredCount = 1 })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await PostAssignmentAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync() })).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task GetRequirements_MissingOccurrence_Returns404()
    {
        (await _client.GetAsync(RequirementsUrl(Guid.NewGuid()))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── assignments ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Host_AssignsNonTeamMember_Returns201()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        var playerId = await SeedPlayerAsync();

        var response = await PostAssignmentAsync(hostId, occurrence.Id, new { playerId });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.AbsolutePath.Should().Be(AssignmentsUrl(occurrence.Id));
        var assignment = (await response.Content.ReadFromJsonAsync<RosterAssignmentDto>())!;
        assignment.OccurrenceId.Should().Be(occurrence.Id);
        assignment.PlayerId.Should().Be(playerId);
        assignment.Username.Should().StartWith("roster-");
        assignment.Status.Should().Be(RosterAssignmentStatus.Confirmed);
        assignment.Source.Should().Be(RosterAssignmentSource.Host);
        assignment.ConfirmedAtUtc.Should().NotBeNull();
        assignment.ConfirmedAtUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
        assignment.ReservedAtUtc.Should().BeNull();
        assignment.RequirementId.Should().BeNull();
        assignment.RoleCode.Should().BeNull();
        assignment.ReplacedAssignmentId.Should().BeNull();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        (await db.TeamMembers.AnyAsync(tm => tm.PlayerId == playerId)).Should().BeFalse();
    }

    [Fact]
    public async Task Host_AssignsTeamMember_Returns201()
    {
        var hostId = Guid.NewGuid();
        var memberId = await SeedPlayerAsync();
        var teamId = await SeedTeamWithMemberAsync(hostId, memberId);
        var occurrence = await SeedEventAsync(hostId, teamId: teamId);

        var assignment = await AssignAsync(hostId, occurrence.Id, new { playerId = memberId });

        assignment.PlayerId.Should().Be(memberId);
    }

    [Theory]
    [InlineData(RosterAssignmentStatus.Reserved)]
    [InlineData(RosterAssignmentStatus.Confirmed)]
    [InlineData(RosterAssignmentStatus.CheckedIn)]
    [InlineData(RosterAssignmentStatus.Active)]
    public async Task SupplyStatus_CountsTowardTheRequirement(RosterAssignmentStatus status)
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        var requirement = await CreateRequirementAsync(hostId, occurrence.Id, new { roleCode = "guard", requiredCount = 2 });

        var assignment = await AssignAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync(), requirementId = requirement.Id, status });

        assignment.Status.Should().Be(status);
        StatusTimestamp(assignment, status).Should().NotBeNull();
        var refreshed = (await GetRequirementsAsync(occurrence.Id)).Single();
        refreshed.SupplyCount.Should().Be(1);
        refreshed.OpenQuantity.Should().Be(1);
    }

    [Theory]
    [InlineData(RosterAssignmentStatus.Departed)]
    [InlineData(RosterAssignmentStatus.NoShow)]
    [InlineData(RosterAssignmentStatus.Cancelled)]
    public async Task HistoricalStatus_DoesNotCount(RosterAssignmentStatus status)
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        var requirement = await CreateRequirementAsync(hostId, occurrence.Id, new { roleCode = "tank", requiredCount = 2 });
        await SeedAssignmentAsync(occurrence.Id, await SeedPlayerAsync(), status, requirement.Id);

        var refreshed = (await GetRequirementsAsync(occurrence.Id)).Single();

        refreshed.SupplyCount.Should().Be(0);
        refreshed.OpenQuantity.Should().Be(2);
    }

    [Theory]
    [InlineData(RosterAssignmentStatus.Departed)]
    [InlineData(RosterAssignmentStatus.NoShow)]
    [InlineData(RosterAssignmentStatus.Cancelled)]
    public async Task CreateAssignment_HistoricalInitialStatus_Returns400(RosterAssignmentStatus status)
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();

        (await PostAssignmentAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync(), status })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task OpenQuantity_IsDerived_AndNeverNegative()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        var requirement = await CreateRequirementAsync(hostId, occurrence.Id, new { roleCode = "center", requiredCount = 1 });

        await AssignAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync(), requirementId = requirement.Id });
        (await GetRequirementsAsync(occurrence.Id)).Single().OpenQuantity.Should().Be(0);

        // Supply above RequiredCount can still exist (e.g. imported rows); open quantity clamps at zero.
        await SeedAssignmentAsync(occurrence.Id, await SeedPlayerAsync(), RosterAssignmentStatus.Active, requirement.Id);
        var refreshed = (await GetRequirementsAsync(occurrence.Id)).Single();
        refreshed.SupplyCount.Should().Be(2);
        refreshed.OpenQuantity.Should().Be(0);
    }

    [Fact]
    public async Task CreateAssignment_IntoAFilledRequirement_Returns409_NoOverbooking()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        var requirement = await CreateRequirementAsync(hostId, occurrence.Id, new { roleCode = "center", requiredCount = 1 });
        await AssignAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync(), requirementId = requirement.Id });

        var response = await PostAssignmentAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync(), requirementId = requirement.Id });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("already filled");
        (await GetRequirementsAsync(occurrence.Id)).Single().SupplyCount.Should().Be(1);
    }

    // ── basketball dogfood: 5-v-5 pickup needs 10 players ───────────────────

    [Fact]
    public async Task Basketball_PickupGame_ReportsZeroFiveNineAndTenOfTenReady()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        occurrence.TeamId.Should().BeNull();
        var participant = await CreateRequirementAsync(hostId, occurrence.Id, new { requiredCount = 10 });
        participant.RoleCode.Should().Be("participant");

        await AssertSummaryAsync(occurrence.Id, supply: 0, open: 10, ready: false);

        // Ten unrelated players, none of them team members.
        var players = new List<Guid>();
        for (var i = 1; i <= 10; i++)
        {
            var playerId = await SeedPlayerAsync();
            players.Add(playerId);
            await AssignAsync(hostId, occurrence.Id, new { playerId, requirementId = participant.Id });

            if (i == 5) await AssertSummaryAsync(occurrence.Id, supply: 5, open: 5, ready: false);
            if (i == 9) await AssertSummaryAsync(occurrence.Id, supply: 9, open: 1, ready: false);
        }

        var summary = await AssertSummaryAsync(occurrence.Id, supply: 10, open: 0, ready: true);
        summary.Assignments.Select(a => a.PlayerId).Should().BeEquivalentTo(players);
        summary.Requirements.Should().ContainSingle().Which.Should().Match<RosterRequirementDto>(r => r.SupplyCount == 10 && r.OpenQuantity == 0);

        // An 11th player is rejected: no overbooking; the roster stays 10/10 READY.
        (await PostAssignmentAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync(), requirementId = participant.Id }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        await AssertSummaryAsync(occurrence.Id, supply: 10, open: 0, ready: true);
    }

    [Theory]
    [InlineData(RosterAssignmentStatus.Departed, RosterExitReason.PlayerLeft)]
    [InlineData(RosterAssignmentStatus.Cancelled, RosterExitReason.Other)]
    [InlineData(RosterAssignmentStatus.NoShow, RosterExitReason.NoShow)]
    public async Task Basketball_PlayerLeaves_SupplyDrops_ReplacementRestoresReadiness_HistoryKept(RosterAssignmentStatus exitStatus, RosterExitReason exitReason)
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        var participant = await CreateRequirementAsync(hostId, occurrence.Id, new { requiredCount = 10 });
        var assignments = new List<RosterAssignmentDto>();
        for (var i = 0; i < 10; i++)
            assignments.Add(await AssignAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync(), requirementId = participant.Id }));
        await AssertSummaryAsync(occurrence.Id, supply: 10, open: 0, ready: true);

        // No transition endpoint exists yet: the exit is recorded on the row directly.
        await RecordExitAsync(assignments[3].Id, exitStatus, exitReason);
        await AssertSummaryAsync(occurrence.Id, supply: 9, open: 1, ready: false);

        var replacement = await AssignAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync(), requirementId = participant.Id, replacedAssignmentId = assignments[3].Id });

        var summary = await AssertSummaryAsync(occurrence.Id, supply: 10, open: 0, ready: true);
        summary.Assignments.Should().HaveCount(11);
        var original = summary.Assignments.Single(a => a.Id == assignments[3].Id);
        original.Status.Should().Be(exitStatus);
        original.ExitReason.Should().Be(exitReason);
        original.PlayerId.Should().Be(assignments[3].PlayerId);
        summary.Assignments.Single(a => a.Id == replacement.Id).ReplacedAssignmentId.Should().Be(assignments[3].Id);
    }

    [Fact]
    public async Task Basketball_TeamGame_MixesTeamMembersAndGuests()
    {
        var hostId = await SeedPlayerAsync();
        var memberId = await SeedPlayerAsync();
        var teamId = await SeedTeamWithMemberAsync(hostId, memberId);
        var occurrence = await SeedEventAsync(hostId, teamId: teamId);
        var participant = await CreateRequirementAsync(hostId, occurrence.Id, new { requiredCount = 3 });

        await AssignAsync(hostId, occurrence.Id, new { playerId = memberId, requirementId = participant.Id });
        await AssignAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync(), requirementId = participant.Id });
        await AssignAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync(), requirementId = participant.Id, sourceRoleLabel = "Sub" });

        await AssertSummaryAsync(occurrence.Id, supply: 3, open: 0, ready: true, required: 3);
    }

    [Fact]
    public async Task Basketball_RoleRequirements_ReadyOnlyWhenEveryPositionIsFilled()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        var guards = await CreateRequirementAsync(hostId, occurrence.Id, new { roleCode = "guard", requiredCount = 4 });
        var forwards = await CreateRequirementAsync(hostId, occurrence.Id, new { roleCode = "forward", requiredCount = 4 });
        var centers = await CreateRequirementAsync(hostId, occurrence.Id, new { roleCode = "center", requiredCount = 2 });

        foreach (var (requirement, count) in new[] { (guards, 4), (forwards, 4), (centers, 1) })
            for (var i = 0; i < count; i++)
                await AssignAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync(), requirementId = requirement.Id });

        await AssertSummaryAsync(occurrence.Id, supply: 9, open: 1, ready: false, required: 10);
        await AssignAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync(), requirementId = centers.Id });
        await AssertSummaryAsync(occurrence.Id, supply: 10, open: 0, ready: true, required: 10);
    }

    [Fact]
    public async Task Summary_WithoutRequirements_IsNotReady_AndIgnoresMaxParticipants()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        await AssignAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync() });

        var summary = await AssertSummaryAsync(occurrence.Id, supply: 1, open: 0, ready: false, required: 0);
        summary.Requirements.Should().BeEmpty();
        summary.Assignments.Should().ContainSingle();
    }

    [Fact]
    public async Task Summary_MissingOccurrence_Returns404()
    {
        (await _client.GetAsync(SummaryUrl(Guid.NewGuid()))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RosterOperations_LeaveLegacyCountAndRosterEntriesUntouched()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        var legacyPlayer = await SeedPlayerAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
            var ev = await db.Events.SingleAsync(e => e.Id == occurrence.Id);
            ev.CurrentParticipantCount = 7;
            db.RosterEntries.Add(new RosterEntry
            {
                Id = Guid.NewGuid(),
                EventId = occurrence.Id,
                PlayerId = legacyPlayer,
                IsConfirmed = true,
                RegisteredAtUtc = DateTime.UtcNow,
                RowVersion = []
            });
            await db.SaveChangesAsync();
        }

        var requirement = await CreateRequirementAsync(hostId, occurrence.Id, new { requiredCount = 10 });
        await AssignAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync(), requirementId = requirement.Id });

        // Legacy state is not authoritative and is never written by roster operations...
        await AssertSummaryAsync(occurrence.Id, supply: 1, open: 9, ready: false);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
            (await db.Events.AsNoTracking().SingleAsync(e => e.Id == occurrence.Id)).CurrentParticipantCount.Should().Be(7);
            (await db.RosterEntries.CountAsync(re => re.EventId == occurrence.Id)).Should().Be(1);
            (await db.RosterAssignments.AnyAsync(a => a.PlayerId == legacyPlayer)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task CreateAssignment_WithRequirement_InheritsItsRoleCode()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        var requirement = await CreateRequirementAsync(hostId, occurrence.Id, new { roleCode = "healer", requiredCount = 2 });

        var assignment = await AssignAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync(), requirementId = requirement.Id, sourceRoleLabel = "Resto" });

        assignment.RequirementId.Should().Be(requirement.Id);
        assignment.RoleCode.Should().Be("healer");
        assignment.SourceRoleLabel.Should().Be("Resto");
    }

    [Fact]
    public async Task CreateAssignment_RoleCodeConflictingWithRequirement_Returns400()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        var requirement = await CreateRequirementAsync(hostId, occurrence.Id, new { roleCode = "healer", requiredCount = 2 });

        var response = await PostAssignmentAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync(), requirementId = requirement.Id, roleCode = "tank" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateAssignment_RequirementOfAnotherOccurrence_Returns400()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        var other = await SeedEventAsync(hostId);
        var foreignRequirement = await CreateRequirementAsync(hostId, other.Id, new { roleCode = "guard", requiredCount = 2 });

        var response = await PostAssignmentAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync(), requirementId = foreignRequirement.Id });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetAssignmentsAsync(occurrence.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAssignment_NullRequirement_IsAllowed_AndCountsOnlyTowardTheOccurrence()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        await CreateRequirementAsync(hostId, occurrence.Id, new { requiredCount = 5 });

        var assignment = await AssignAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync(), roleCode = "participant" });

        assignment.RequirementId.Should().BeNull();
        assignment.RoleCode.Should().Be("participant");
        (await GetRequirementsAsync(occurrence.Id)).Single().SupplyCount.Should().Be(0);
    }

    [Fact]
    public async Task CreateAssignment_UnknownPlayer_Returns400()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();

        (await PostAssignmentAsync(hostId, occurrence.Id, new { playerId = Guid.NewGuid() })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateAssignment_MissingOrEmptyPlayerId_Returns400()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();

        (await PostAssignmentAsync(hostId, occurrence.Id, new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PostAssignmentAsync(hostId, occurrence.Id, new { playerId = Guid.Empty })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateAssignment_DuplicateSupplyForPlayer_Returns409()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        var playerId = await SeedPlayerAsync();
        await AssignAsync(hostId, occurrence.Id, new { playerId, status = RosterAssignmentStatus.Reserved });

        var response = await PostAssignmentAsync(hostId, occurrence.Id, new { playerId, status = RosterAssignmentStatus.Active });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await GetAssignmentsAsync(occurrence.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task CreateAssignment_SamePlayerOnAnotherOccurrence_IsAllowed()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        var other = await SeedEventAsync(hostId);
        var playerId = await SeedPlayerAsync();

        await AssignAsync(hostId, occurrence.Id, new { playerId });
        await AssignAsync(hostId, other.Id, new { playerId });
    }

    [Fact]
    public async Task Replacement_AfterDeparture_IsAllowed_AndPreservesTheOriginalRow()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        var requirement = await CreateRequirementAsync(hostId, occurrence.Id, new { roleCode = "guard", requiredCount = 1 });
        var departedPlayer = await SeedPlayerAsync();
        var original = await SeedAssignmentAsync(occurrence.Id, departedPlayer, RosterAssignmentStatus.Departed, requirement.Id, RosterExitReason.PlayerLeft);

        var replacement = await AssignAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync(), requirementId = requirement.Id, replacedAssignmentId = original.Id });

        replacement.ReplacedAssignmentId.Should().Be(original.Id);
        replacement.Id.Should().NotBe(original.Id);

        var assignments = await GetAssignmentsAsync(occurrence.Id);
        assignments.Should().HaveCount(2);
        var originalAfter = assignments.Single(a => a.Id == original.Id);
        originalAfter.PlayerId.Should().Be(departedPlayer);
        originalAfter.Status.Should().Be(RosterAssignmentStatus.Departed);
        originalAfter.ExitReason.Should().Be(RosterExitReason.PlayerLeft);
        originalAfter.ReplacedAssignmentId.Should().BeNull();
        (await GetRequirementsAsync(occurrence.Id)).Single().SupplyCount.Should().Be(1);
    }

    [Fact]
    public async Task Replacement_DepartedPlayerMayRejoin_AsANewRow()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        var playerId = await SeedPlayerAsync();
        var departed = await SeedAssignmentAsync(occurrence.Id, playerId, RosterAssignmentStatus.Departed);

        var rejoined = await AssignAsync(hostId, occurrence.Id, new { playerId, replacedAssignmentId = departed.Id });

        rejoined.Id.Should().NotBe(departed.Id);
        (await GetAssignmentsAsync(occurrence.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Replacement_OfAssignmentStillHoldingSupply_Returns409()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        var current = await AssignAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync() });

        var response = await PostAssignmentAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync(), replacedAssignmentId = current.Id });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Replacement_OfAssignmentOfAnotherOccurrence_Returns400()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        var other = await SeedEventAsync(hostId);
        var foreign = await SeedAssignmentAsync(other.Id, await SeedPlayerAsync(), RosterAssignmentStatus.NoShow);

        var response = await PostAssignmentAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync(), replacedAssignmentId = foreign.Id });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateAssignment_NonHost_Returns403()
    {
        var (_, occurrence) = await SeedHostedEventAsync();

        var response = await PostAssignmentAsync(Guid.NewGuid(), occurrence.Id, new { playerId = await SeedPlayerAsync() });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAssignmentsAsync(occurrence.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAssignment_PlayerCannotSelfAssign_WhenNotHost()
    {
        var (_, occurrence) = await SeedHostedEventAsync();
        var playerId = await SeedPlayerAsync();

        (await PostAssignmentAsync(playerId, occurrence.Id, new { playerId })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateAssignment_OrphanedOccurrence_Returns409()
    {
        var occurrence = await SeedEventAsync(hostId: null);

        (await PostAssignmentAsync(Guid.NewGuid(), occurrence.Id, new { playerId = await SeedPlayerAsync() })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateAssignment_MissingOccurrence_Returns404()
    {
        (await PostAssignmentAsync(Guid.NewGuid(), Guid.NewGuid(), new { playerId = await SeedPlayerAsync() })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateAssignment_Unauthenticated_Returns401()
    {
        var (_, occurrence) = await SeedHostedEventAsync();

        (await _client.PostAsJsonAsync(AssignmentsUrl(occurrence.Id), new { playerId = Guid.NewGuid() })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PublicReads_AreAnonymous_AndExposeNoEmailOrIdentityData()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        var playerId = await SeedPlayerAsync(email: "secret-roster@example.com");
        await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, playerId);
        var requirement = await CreateRequirementAsync(hostId, occurrence.Id, new { roleCode = "guard", requiredCount = 2 });
        await AssignAsync(hostId, occurrence.Id, new { playerId, requirementId = requirement.Id });

        var assignmentsJson = await _client.GetStringAsync(AssignmentsUrl(occurrence.Id));
        var requirementsJson = await _client.GetStringAsync(RequirementsUrl(occurrence.Id));
        var summaryJson = await _client.GetStringAsync(SummaryUrl(occurrence.Id));

        foreach (var json in new[] { assignmentsJson, requirementsJson, summaryJson })
        {
            json.Should().NotContain("secret-roster@example.com");
            json.Should().NotContainEquivalentOf("email");
            json.Should().NotContainEquivalentOf("subject");
            json.Should().NotContainEquivalentOf("issuer");
            json.Should().NotContainEquivalentOf("tenant");
            json.Should().NotContainEquivalentOf("provider");
            json.Should().NotContain(TeamBuilderWebApplicationFactory.TestIssuer);
        }

        using var document = JsonDocument.Parse(assignmentsJson);
        var item = document.RootElement.GetProperty("items")[0];
        item.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "occurrenceId", "playerId", "username", "displayName", "requirementId", "roleCode", "sourceRoleLabel",
            "status", "source", "reservedAtUtc", "confirmedAtUtc", "checkedInAtUtc", "activatedAtUtc",
            "departedAtUtc", "exitReason", "replacedAssignmentId", "createdAtUtc", "updatedAtUtc");
    }

    [Fact]
    public async Task GetAssignments_IsPaginated_AndIncludesHistory()
    {
        var (hostId, occurrence) = await SeedHostedEventAsync();
        await SeedAssignmentAsync(occurrence.Id, await SeedPlayerAsync(), RosterAssignmentStatus.Cancelled);
        for (var i = 0; i < 3; i++)
            await AssignAsync(hostId, occurrence.Id, new { playerId = await SeedPlayerAsync() });

        var page = (await _client.GetFromJsonAsync<PaginatedResult<RosterAssignmentDto>>($"{AssignmentsUrl(occurrence.Id)}?page=2&pageSize=3"))!;

        page.TotalCount.Should().Be(4);
        page.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task GetAssignments_MissingOccurrence_Returns404()
    {
        (await _client.GetAsync(AssignmentsUrl(Guid.NewGuid()))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string RequirementsUrl(Guid occurrenceId) => $"/api/v1/events/{occurrenceId}/roster/requirements";
    private static string AssignmentsUrl(Guid occurrenceId) => $"/api/v1/events/{occurrenceId}/roster/assignments";
    private static string SummaryUrl(Guid occurrenceId) => $"/api/v1/events/{occurrenceId}/roster";

    private async Task<RosterSummaryDto> AssertSummaryAsync(Guid occurrenceId, int supply, int open, bool ready, int required = 10)
    {
        var summary = (await _client.GetFromJsonAsync<RosterSummaryDto>(SummaryUrl(occurrenceId)))!;
        summary.OccurrenceId.Should().Be(occurrenceId);
        summary.RequiredCount.Should().Be(required);
        summary.SupplyCount.Should().Be(supply);
        summary.OpenQuantity.Should().Be(open);
        summary.IsRosterReady.Should().Be(ready);
        return summary;
    }

    private async Task RecordExitAsync(Guid assignmentId, RosterAssignmentStatus status, RosterExitReason reason)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var assignment = await db.RosterAssignments.SingleAsync(a => a.Id == assignmentId);
        assignment.Status = status;
        assignment.ExitReason = reason;
        assignment.DepartedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private static DateTime? StatusTimestamp(RosterAssignmentDto assignment, RosterAssignmentStatus status) => status switch
    {
        RosterAssignmentStatus.Reserved => assignment.ReservedAtUtc,
        RosterAssignmentStatus.Confirmed => assignment.ConfirmedAtUtc,
        RosterAssignmentStatus.CheckedIn => assignment.CheckedInAtUtc,
        RosterAssignmentStatus.Active => assignment.ActivatedAtUtc,
        _ => null
    };

    private async Task<(Guid HostId, EventOccurrence Occurrence)> SeedHostedEventAsync(EventStatus status = EventStatus.Planned)
    {
        var hostId = await SeedPlayerAsync();
        return (hostId, await SeedEventAsync(hostId, status));
    }

    private async Task<EventOccurrence> SeedEventAsync(Guid? hostId, EventStatus status = EventStatus.Planned, Guid? teamId = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var occurrence = new EventOccurrence
        {
            Id = Guid.NewGuid(),
            Name = "Pickup run",
            ScheduledStartUtc = DateTime.UtcNow.AddDays(3),
            Status = status,
            MaxParticipants = 20,
            HostId = hostId,
            TeamId = teamId,
            RowVersion = []
        };
        db.Events.Add(occurrence);
        await db.SaveChangesAsync();
        return occurrence;
    }

    private async Task<Guid> SeedPlayerAsync(string? email = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = $"roster-{Guid.NewGuid():N}",
            Email = email,
            RowVersion = []
        };
        db.Players.Add(player);
        await db.SaveChangesAsync();
        return player.Id;
    }

    private async Task<Guid> SeedTeamWithMemberAsync(Guid ownerId, Guid memberId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        if (!await db.Players.AnyAsync(p => p.Id == ownerId))
            db.Players.Add(new Player { Id = ownerId, Username = $"owner-{Guid.NewGuid():N}", RowVersion = [] });
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Owls",
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

    /// <summary>Seeds a row directly (e.g. historical statuses, which no endpoint creates yet).</summary>
    private async Task<RosterAssignment> SeedAssignmentAsync(
        Guid occurrenceId,
        Guid playerId,
        RosterAssignmentStatus status,
        Guid? requirementId = null,
        RosterExitReason? exitReason = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var assignment = new RosterAssignment
        {
            Id = Guid.NewGuid(),
            OccurrenceId = occurrenceId,
            PlayerId = playerId,
            RequirementId = requirementId,
            Status = status,
            Source = RosterAssignmentSource.Host,
            ConfirmedAtUtc = DateTime.UtcNow.AddHours(-2),
            DepartedAtUtc = status == RosterAssignmentStatus.Departed ? DateTime.UtcNow.AddHours(-1) : null,
            ExitReason = exitReason,
            RowVersion = []
        };
        db.RosterAssignments.Add(assignment);
        await db.SaveChangesAsync();
        return assignment;
    }

    private async Task<HttpResponseMessage> PostRequirementAsync(Guid playerId, Guid occurrenceId, object body) =>
        await SendAsync(HttpMethod.Post, RequirementsUrl(occurrenceId), await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, playerId), body);

    private async Task<HttpResponseMessage> PostAssignmentAsync(Guid playerId, Guid occurrenceId, object body) =>
        await SendAsync(HttpMethod.Post, AssignmentsUrl(occurrenceId), await LinkedPlayerTokens.ForPlayerAsync(_factory.Services, playerId), body);

    private async Task<RosterRequirementDto> CreateRequirementAsync(Guid hostId, Guid occurrenceId, object body)
    {
        var response = await PostRequirementAsync(hostId, occurrenceId, body);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RosterRequirementDto>())!;
    }

    private async Task<RosterAssignmentDto> AssignAsync(Guid hostId, Guid occurrenceId, object body)
    {
        var response = await PostAssignmentAsync(hostId, occurrenceId, body);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RosterAssignmentDto>())!;
    }

    private async Task<List<RosterRequirementDto>> GetRequirementsAsync(Guid occurrenceId) =>
        (await _client.GetFromJsonAsync<List<RosterRequirementDto>>(RequirementsUrl(occurrenceId)))!;

    private async Task<List<RosterAssignmentDto>> GetAssignmentsAsync(Guid occurrenceId) =>
        (await _client.GetFromJsonAsync<PaginatedResult<RosterAssignmentDto>>($"{AssignmentsUrl(occurrenceId)}?pageSize=100"))!.Items.ToList();

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }
}
