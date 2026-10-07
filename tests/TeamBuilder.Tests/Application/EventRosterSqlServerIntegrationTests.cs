using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Domain;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Data.Configurations;
using TeamBuilder.Infrastructure.Persistence;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Roster requirements and assignments on a real, fully migrated SQL Server database: the
/// filtered supply index, the same-occurrence composite foreign keys, the self-replacement
/// CHECK, deterministic duplicate races, history-preserving event delete, player-delete protection and host lifecycle interleavings.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class EventRosterSqlServerIntegrationTests : IAsyncLifetime
{
    private const int UniqueIndexViolation = 2601;
    private const int ConstraintViolation = 547;

    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public EventRosterSqlServerIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>A host player shared by tests that only need some valid host.</summary>
    private Guid HostId { get; set; }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "roster");
        await _db.MigrateToAsync();
        HostId = await AddPlayerAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ── schema ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SupplyIndex_IsAUniqueFilteredIndexOnOccurrenceAndPlayer()
    {
        (await ScalarAsync<bool>($"SELECT [is_unique] FROM sys.indexes WHERE [name] = N'{RosterAssignmentConfiguration.SupplyUniqueIndexName}'")).Should().BeTrue();
        (await ScalarAsync<bool>($"SELECT [has_filter] FROM sys.indexes WHERE [name] = N'{RosterAssignmentConfiguration.SupplyUniqueIndexName}'")).Should().BeTrue();

        var filter = (await ScalarAsync<string>($"SELECT [filter_definition] FROM sys.indexes WHERE [name] = N'{RosterAssignmentConfiguration.SupplyUniqueIndexName}'"))!;
        foreach (var value in new[] { 1, 2, 3, 4 })
            filter.Should().Contain($"({value})");
        foreach (var value in new[] { 5, 6, 7 })
            filter.Should().NotContain($"({value})");

        var columns = await ColumnsOfIndexAsync(RosterAssignmentConfiguration.SupplyUniqueIndexName);
        columns.Should().Equal("OccurrenceId", "PlayerId");
    }

    [Fact]
    public async Task CompositeForeignKeys_ReferenceIdAndOccurrence()
    {
        (await ForeignKeyColumnsAsync(RosterAssignmentConfiguration.RequirementForeignKeyName))
            .Should().Equal(("RequirementId", "Id"), ("OccurrenceId", "OccurrenceId"));
        (await ForeignKeyColumnsAsync(RosterAssignmentConfiguration.ReplacedAssignmentForeignKeyName))
            .Should().Equal(("ReplacedAssignmentId", "Id"), ("OccurrenceId", "OccurrenceId"));
    }

    [Fact]
    public async Task NoForeignKeyRelatesRosterAssignmentsToTeamMembers()
    {
        (await ScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.foreign_keys
            WHERE [parent_object_id] = OBJECT_ID(N'[RosterAssignments]')
              AND [referenced_object_id] = OBJECT_ID(N'[TeamMembers]')
            """)).Should().Be(0);
    }

    // ── requirements ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(100001)]
    public async Task RequiredCountCheck_RejectsOutOfRangeValues(int requiredCount)
    {
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());

        var act = () => AddRequirementAsync(occurrenceId, "guard", requiredCount);

        (await act.Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<SqlException>()
            .Which.Number.Should().Be(ConstraintViolation);
    }

    [Fact]
    public async Task RoleUniqueIndex_RejectsADuplicateRoleOnTheSameOccurrence_ButNotAcrossOccurrences()
    {
        var hostId = await AddPlayerAsync();
        var first = await AddOccurrenceAsync(hostId);
        var second = await AddOccurrenceAsync(hostId);
        await AddRequirementAsync(first, "guard", 2);
        await AddRequirementAsync(second, "guard", 2);

        var act = () => AddRequirementAsync(first, "guard", 1);

        var ex = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        RosterConflictClassifier.IsDuplicateRequirementRole(ex).Should().BeTrue();
        RosterConflictClassifier.IsDuplicateSupplyAssignment(ex).Should().BeFalse();
    }

    [Fact]
    public async Task CreateRequirement_ConcurrentDuplicateRole_IsAnOrderly409()
    {
        var hostId = await AddPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);

        await using var context = InterferingContext(() => AddRequirementAsync(occurrenceId, "healer", 3));
        var service = new EventRosterService(context, TimeProvider.System);

        var act = () => service.CreateRequirementAsync(occurrenceId, new CreateRosterRequirementDto { RoleCode = "healer", RequiredCount = 4 }, hostId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage(EventRosterService.DuplicateRequirementMessage)
            .Which.InnerException.Should().BeOfType<DbUpdateException>();
        await using var verify = _db.CreateContext();
        (await verify.RosterRequirements.Where(r => r.OccurrenceId == occurrenceId).Select(r => r.RequiredCount).ToListAsync())
            .Should().Equal(3);
    }

    // ── supply index ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RosterAssignmentStatus.Reserved, RosterAssignmentStatus.Confirmed)]
    [InlineData(RosterAssignmentStatus.Confirmed, RosterAssignmentStatus.Confirmed)]
    [InlineData(RosterAssignmentStatus.CheckedIn, RosterAssignmentStatus.Active)]
    [InlineData(RosterAssignmentStatus.Active, RosterAssignmentStatus.Reserved)]
    public async Task SupplyIndex_RejectsASecondSupplyAssignmentForTheSamePlayer(RosterAssignmentStatus existing, RosterAssignmentStatus second)
    {
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());
        var playerId = await AddPlayerAsync();
        await AddAssignmentAsync(occurrenceId, playerId, existing);

        var act = () => AddAssignmentAsync(occurrenceId, playerId, second);

        var ex = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        ex.InnerException.Should().BeOfType<SqlException>().Which.Number.Should().Be(UniqueIndexViolation);
        RosterConflictClassifier.IsDuplicateSupplyAssignment(ex).Should().BeTrue();
    }

    [Fact]
    public async Task SupplyIndex_AllowsHistoryAndANewSupplyAssignmentAfterIt()
    {
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());
        var playerId = await AddPlayerAsync();

        await AddAssignmentAsync(occurrenceId, playerId, RosterAssignmentStatus.Departed);
        await AddAssignmentAsync(occurrenceId, playerId, RosterAssignmentStatus.NoShow);
        await AddAssignmentAsync(occurrenceId, playerId, RosterAssignmentStatus.Cancelled);
        await AddAssignmentAsync(occurrenceId, playerId, RosterAssignmentStatus.Cancelled);
        await AddAssignmentAsync(occurrenceId, playerId, RosterAssignmentStatus.Confirmed);

        await using var context = _db.CreateContext();
        (await context.RosterAssignments.CountAsync(a => a.OccurrenceId == occurrenceId && a.PlayerId == playerId)).Should().Be(5);
    }

    [Fact]
    public async Task SupplyIndex_AllowsTheSamePlayerOnDifferentOccurrences()
    {
        var hostId = await AddPlayerAsync();
        var playerId = await AddPlayerAsync();

        await AddAssignmentAsync(await AddOccurrenceAsync(hostId), playerId, RosterAssignmentStatus.Active);
        await AddAssignmentAsync(await AddOccurrenceAsync(hostId), playerId, RosterAssignmentStatus.Active);
    }

    [Fact]
    public async Task SupplyIndex_LeavingSupplyFreesTheSpotForANewRow()
    {
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());
        var playerId = await AddPlayerAsync();
        var first = await AddAssignmentAsync(occurrenceId, playerId, RosterAssignmentStatus.Active);

        await using (var context = _db.CreateContext())
        {
            var row = await context.RosterAssignments.SingleAsync(a => a.Id == first);
            row.Status = RosterAssignmentStatus.Departed;
            row.DepartedAtUtc = DateTime.UtcNow;
            row.ExitReason = RosterExitReason.PlayerLeft;
            await context.SaveChangesAsync();
        }

        await AddAssignmentAsync(occurrenceId, playerId, RosterAssignmentStatus.Reserved, replacedAssignmentId: first);
    }

    [Fact]
    public async Task CreateAssignment_ConcurrentDuplicateSupply_IsAnOrderly409()
    {
        var hostId = await AddPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        var playerId = await AddPlayerAsync();

        // A competing request commits the same player's supply row after this request's
        // duplicate pre-check but before its INSERT: the filtered unique index decides.
        await using var context = InterferingContext(() => AddAssignmentAsync(occurrenceId, playerId, RosterAssignmentStatus.Reserved));
        var service = new EventRosterService(context, TimeProvider.System);

        var act = () => service.CreateAssignmentAsync(occurrenceId, new CreateRosterAssignmentDto { PlayerId = playerId, RequirementId = requirementId }, hostId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage(EventRosterService.DuplicateSupplyAssignmentMessage)
            .Which.InnerException.Should().BeOfType<DbUpdateException>();
        await using var verify = _db.CreateContext();
        (await verify.RosterAssignments.Where(a => a.OccurrenceId == occurrenceId).Select(a => a.Status).ToListAsync())
            .Should().Equal(RosterAssignmentStatus.Reserved);
    }

    [Fact]
    public async Task CreateAssignment_ParallelDuplicates_ExactlyOneWins()
    {
        var hostId = await AddPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        var playerId = await AddPlayerAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var context = _db.CreateContext();
            try
            {
                await new EventRosterService(context, TimeProvider.System)
                    .CreateAssignmentAsync(occurrenceId, new CreateRosterAssignmentDto { PlayerId = playerId, RequirementId = requirementId }, hostId);
                return true;
            }
            catch (RosterConflictException ex) when (ex.Code is RosterConflictCodes.AlreadyParticipating or RosterConflictCodes.RosterChanged)
            {
                return false;
            }
        }));

        results.Count(r => r).Should().Be(1);
        await using var verify = _db.CreateContext();
        (await verify.RosterAssignments.CountAsync(a => a.OccurrenceId == occurrenceId)).Should().Be(1);
    }

    // ── requirement capacity (no overbooking) ────────────────────────────────

    [Fact]
    public async Task CreateAssignment_ConcurrentFillOfTheLastOpenQuantity_IsAnOrderly409()
    {
        var occurrenceId = await AddOccurrenceAsync(HostId);
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        for (var i = 0; i < 9; i++)
            await AssignViaServiceAsync(occurrenceId, await AddPlayerAsync(), requirementId);

        // Another host request takes the 10th spot after this request counted 9 but before it
        // commits: the forced RowVersion-checked requirement UPDATE makes this save fail, and
        // the bounded re-validation re-reads the roster and reports a deterministic "filled".
        var competitor = await AddPlayerAsync();
        var playerId = await AddPlayerAsync();
        await using var context = InterferingContext(() => AssignViaServiceAsync(occurrenceId, competitor, requirementId));
        var service = new EventRosterService(context, TimeProvider.System);

        var act = () => service.CreateAssignmentAsync(
            occurrenceId,
            new CreateRosterAssignmentDto { PlayerId = playerId, RequirementId = requirementId },
            HostId);

        (await act.Should().ThrowAsync<RosterConflictException>())
            .WithMessage(EventRosterService.RequirementFilledMessage)
            .Which.Code.Should().Be(RosterConflictCodes.RequirementFull);
        (await SupplyOfRequirementAsync(requirementId)).Should().Be(10);
    }

    [Fact]
    public async Task CreateAssignment_ConcurrentFillWithCapacityLeft_RevalidatesAndCommits()
    {
        var occurrenceId = await AddOccurrenceAsync(HostId);
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        for (var i = 0; i < 5; i++)
            await AssignViaServiceAsync(occurrenceId, await AddPlayerAsync(), requirementId);

        // The competitor bumps the requirement RowVersion but leaves open quantity. Host
        // assignment re-runs every check on fresh state (host authority, status, capacity),
        // so the loss resolves to a commit instead of surfacing as RosterChanged.
        var competitor = await AddPlayerAsync();
        var playerId = await AddPlayerAsync();
        await using var context = InterferingContext(() => AssignViaServiceAsync(occurrenceId, competitor, requirementId));
        var service = new EventRosterService(context, TimeProvider.System);
        var dto = new CreateRosterAssignmentDto { PlayerId = playerId, RequirementId = requirementId };

        var assignment = await service.CreateAssignmentAsync(occurrenceId, dto, HostId);

        assignment.PlayerId.Should().Be(playerId);
        (await SupplyOfRequirementAsync(requirementId)).Should().Be(7);
    }

    // ── self-claim / leave interleavings (shared allocation path) ────────────

    [Fact]
    public async Task Claim_HostAssignsTheLastSpotFirst_ClaimLosesWithRequirementFull()
    {
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        for (var i = 0; i < 9; i++)
            await ClaimViaServiceAsync(occurrenceId, await AddPlayerAsync(), requirementId);

        var assigned = await AddPlayerAsync();
        var claimant = await AddPlayerAsync();
        await using var context = InterferingContext(() => AssignViaServiceAsync(occurrenceId, assigned, requirementId));
        var service = new EventRosterService(context, TimeProvider.System);

        var act = () => service.ClaimAsync(occurrenceId, claimant, new ClaimRosterSpotDto { RequirementId = requirementId });

        (await act.Should().ThrowAsync<RosterConflictException>()).Which.Code.Should().Be(RosterConflictCodes.RequirementFull);
        (await SupplyOfRequirementAsync(requirementId)).Should().Be(10);
    }

    [Fact]
    public async Task Claim_TheSamePlayerCommitsFirst_IsReturnedAsTheExistingClaim()
    {
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        var playerId = await AddPlayerAsync();
        Guid committedId = default;

        // The twin request commits between this request's checks and its save: the filtered
        // index / RowVersion stops the second row and the claim returns the committed one.
        await using var context = InterferingContext(async () => committedId = await ClaimViaServiceAsync(occurrenceId, playerId, requirementId));
        var result = await new EventRosterService(context, TimeProvider.System)
            .ClaimAsync(occurrenceId, playerId, new ClaimRosterSpotDto { RequirementId = requirementId });

        result.Created.Should().BeFalse();
        result.Assignment.Id.Should().Be(committedId);
        (await SupplyOfRequirementAsync(requirementId)).Should().Be(1);
    }

    [Fact]
    public async Task Claim_WhileAnotherPlayerLeaves_CommitsAndReadinessFollowsBoth()
    {
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        var leaver = await AddPlayerAsync();
        var leaverAssignment = await ClaimViaServiceAsync(occurrenceId, leaver, requirementId);
        for (var i = 0; i < 8; i++)
            await ClaimViaServiceAsync(occurrenceId, await AddPlayerAsync(), requirementId);

        // A leave commits after the claim counted 9 but before it saves. Leaving never adds
        // supply, so it does not touch the requirement RowVersion and the claim still commits.
        var claimant = await AddPlayerAsync();
        await using var context = InterferingContext(async () =>
        {
            await using var leaveContext = _db.CreateContext();
            await new EventRosterService(leaveContext, TimeProvider.System).LeaveAsync(occurrenceId, leaverAssignment, leaver);
        });
        var result = await new EventRosterService(context, TimeProvider.System)
            .ClaimAsync(occurrenceId, claimant, new ClaimRosterSpotDto { RequirementId = requirementId });

        result.Created.Should().BeTrue();
        (await SupplyOfRequirementAsync(requirementId)).Should().Be(9);
        var summary = await new EventRosterService(_db.CreateContext(), TimeProvider.System).GetSummaryAsync(occurrenceId);
        (summary!.SupplyCount, summary.OpenQuantity, summary.IsRosterReady).Should().Be((9, 1, false));
        summary.Assignments.Should().HaveCount(10);
    }

    [Fact]
    public async Task Leave_WhileTheHostRemovesTheSameAssignment_EndsItOnce()
    {
        var hostId = await AddPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        var playerId = await AddPlayerAsync();
        var assignmentId = await ClaimViaServiceAsync(occurrenceId, playerId, requirementId);

        await using var context = InterferingContext(async () =>
        {
            await using var hostContext = _db.CreateContext();
            await new EventRosterService(hostContext, TimeProvider.System).RemoveAsync(occurrenceId, assignmentId, hostId);
        });
        var act = () => new EventRosterService(context, TimeProvider.System).LeaveAsync(occurrenceId, assignmentId, playerId);

        (await act.Should().ThrowAsync<RosterConflictException>()).Which.Code.Should().Be(RosterConflictCodes.AssignmentEnded);
        await using var verify = _db.CreateContext();
        var row = await verify.RosterAssignments.SingleAsync(a => a.Id == assignmentId);
        (row.Status, row.ExitReason).Should().Be((RosterAssignmentStatus.Cancelled, RosterExitReason.HostRemoved));
    }

    [Fact]
    public async Task CreateAssignment_ParallelFills_NeverExceedRequiredCount()
    {
        var occurrenceId = await AddOccurrenceAsync(HostId);
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        for (var i = 0; i < 8; i++)
            await AssignViaServiceAsync(occurrenceId, await AddPlayerAsync(), requirementId);
        var candidates = new List<Guid>();
        for (var i = 0; i < 8; i++)
            candidates.Add(await AddPlayerAsync());

        var results = await Task.WhenAll(candidates.Select(async playerId =>
        {
            await using var context = _db.CreateContext();
            try
            {
                await new EventRosterService(context, TimeProvider.System).CreateAssignmentAsync(
                    occurrenceId,
                    new CreateRosterAssignmentDto { PlayerId = playerId, RequirementId = requirementId },
                    HostId);
                return true;
            }
            catch (RosterConflictException ex) when (ex.Code is RosterConflictCodes.RequirementFull or RosterConflictCodes.RosterChanged)
            {
                return false;
            }
        }));

        var supply = await SupplyOfRequirementAsync(requirementId);
        supply.Should().BeLessThanOrEqualTo(10);
        supply.Should().Be(8 + results.Count(r => r));
        results.Count(r => r).Should().BeGreaterThan(0);

        // Whatever lost a race can retry until the requirement is full, and never beyond it.
        foreach (var playerId in candidates.Where((_, i) => !results[i]))
        {
            try { await AssignViaServiceAsync(occurrenceId, playerId, requirementId); }
            catch (InvalidOperationException ex) when (ex.Message == EventRosterService.RequirementFilledMessage) { }
        }
        (await SupplyOfRequirementAsync(requirementId)).Should().Be(10);
    }

    [Fact]
    public async Task CreateAssignment_AfterADeparture_MayRefillTheRequirement()
    {
        var occurrenceId = await AddOccurrenceAsync(HostId);
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 1);
        var first = await AssignViaServiceAsync(occurrenceId, await AddPlayerAsync(), requirementId);
        await ExecuteAsync($"UPDATE [RosterAssignments] SET [Status] = 5, [ExitReason] = 1, [DepartedAtUtc] = SYSUTCDATETIME() WHERE [Id] = '{first}'");

        await new EventRosterService(_db.CreateContext(), TimeProvider.System).CreateAssignmentAsync(
            occurrenceId,
            new CreateRosterAssignmentDto { PlayerId = await AddPlayerAsync(), RequirementId = requirementId, ReplacedAssignmentId = first },
            HostId);

        (await SupplyOfRequirementAsync(requirementId)).Should().Be(1);
    }

    // ── read shape ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Summary_UsesAFixedNumberOfQueries_AndSelectsNoPrivatePlayerColumns()
    {
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        for (var i = 0; i < 10; i++)
            await AssignViaServiceAsync(occurrenceId, await AddPlayerAsync(), requirementId);
        await AddAssignmentAsync(occurrenceId, await AddPlayerAsync(), RosterAssignmentStatus.Departed);

        var recorder = new CommandRecorder();
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseSqlServer(_db.ConnectionString)
            .AddInterceptors(recorder)
            .Options;
        await using var context = new TeamBuilderDbContext(options);

        var summary = await new EventRosterService(context, TimeProvider.System).GetSummaryAsync(occurrenceId);

        summary!.IsRosterReady.Should().BeTrue();
        summary.SupplyCount.Should().Be(10);
        summary.Assignments.Should().HaveCount(11);
        summary.Assignments.Should().OnlyContain(a => a.Username.StartsWith("p-") && a.CreatedAtUtc.Kind == DateTimeKind.Utc);
        recorder.Commands.Should().HaveCount(3, "existence, requirements and one projected assignment query");
        recorder.Commands.Should().NotContain(c => c.Contains("[Email]") || c.Contains("PlayerIdentities"));
    }

    // ── requirement relationship ─────────────────────────────────────────────

    [Fact]
    public async Task RequirementForeignKey_RejectsARequirementOfAnotherOccurrence()
    {
        var hostId = await AddPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);
        var otherOccurrenceId = await AddOccurrenceAsync(hostId);
        var foreignRequirement = await AddRequirementAsync(otherOccurrenceId, "guard", 2);

        var act = async () => await AddAssignmentAsync(occurrenceId, await AddPlayerAsync(), RosterAssignmentStatus.Confirmed, requirementId: foreignRequirement);

        var sql = (await act.Should().ThrowAsync<DbUpdateException>()).Which.InnerException.Should().BeOfType<SqlException>().Which;
        sql.Number.Should().Be(ConstraintViolation);
        sql.Message.Should().Contain(RosterAssignmentConfiguration.RequirementForeignKeyName);
    }

    [Fact]
    public async Task RequirementForeignKey_AllowsNullAndASameOccurrenceRequirement()
    {
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());
        var requirementId = await AddRequirementAsync(occurrenceId, "guard", 2);

        await AddAssignmentAsync(occurrenceId, await AddPlayerAsync(), RosterAssignmentStatus.Confirmed, requirementId: null);
        await AddAssignmentAsync(occurrenceId, await AddPlayerAsync(), RosterAssignmentStatus.Confirmed, requirementId: requirementId);

        var requirements = await new EventRosterService(_db.CreateContext(), TimeProvider.System).GetRequirementsAsync(occurrenceId);
        requirements!.Single().Should().Match<RosterRequirementDto>(r => r.SupplyCount == 1 && r.OpenQuantity == 1);
    }

    [Fact]
    public async Task RequirementInUse_CannotBeDeleted()
    {
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());
        var requirementId = await AddRequirementAsync(occurrenceId, "guard", 2);
        await AddAssignmentAsync(occurrenceId, await AddPlayerAsync(), RosterAssignmentStatus.Departed, requirementId: requirementId);

        var act = () => ExecuteAsync($"DELETE FROM [RosterRequirements] WHERE [Id] = '{requirementId}'");

        (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(ConstraintViolation);
    }

    // ── replacement history ──────────────────────────────────────────────────

    [Fact]
    public async Task Replacement_PreservesTheOriginalRowUnchanged()
    {
        var occurrenceId = await AddOccurrenceAsync(HostId);
        var requirementId = await AddRequirementAsync(occurrenceId, "tank", 1);
        var departedPlayer = await AddPlayerAsync();
        var original = await AddAssignmentAsync(occurrenceId, departedPlayer, RosterAssignmentStatus.NoShow, requirementId: requirementId);
        byte[] rowVersionBefore;
        await using (var context = _db.CreateContext())
            rowVersionBefore = (await context.RosterAssignments.AsNoTracking().SingleAsync(a => a.Id == original)).RowVersion;

        var replacement = await new EventRosterService(_db.CreateContext(), TimeProvider.System).CreateAssignmentAsync(
            occurrenceId,
            new CreateRosterAssignmentDto { PlayerId = await AddPlayerAsync(), RequirementId = requirementId, ReplacedAssignmentId = original },
            HostId);

        await using var verify = _db.CreateContext();
        var originalAfter = await verify.RosterAssignments.AsNoTracking().SingleAsync(a => a.Id == original);
        originalAfter.RowVersion.Should().Equal(rowVersionBefore);
        originalAfter.Status.Should().Be(RosterAssignmentStatus.NoShow);
        originalAfter.PlayerId.Should().Be(departedPlayer);
        originalAfter.ReplacedAssignmentId.Should().BeNull();
        (await verify.RosterAssignments.AsNoTracking().SingleAsync(a => a.Id == replacement.Id)).ReplacedAssignmentId.Should().Be(original);
    }

    [Fact]
    public async Task Replacement_CannotReferenceItself()
    {
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());
        var id = Guid.NewGuid();

        var act = async () => await AddAssignmentAsync(occurrenceId, await AddPlayerAsync(), RosterAssignmentStatus.Confirmed, id: id, replacedAssignmentId: id);

        var sql = (await act.Should().ThrowAsync<DbUpdateException>()).Which.InnerException.Should().BeOfType<SqlException>().Which;
        sql.Number.Should().Be(ConstraintViolation);
        sql.Message.Should().Contain(RosterAssignmentConfiguration.NotSelfReplacementCheckName);
    }

    [Fact]
    public async Task Replacement_CannotReferenceAnAssignmentOfAnotherOccurrence()
    {
        var hostId = await AddPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);
        var foreign = await AddAssignmentAsync(await AddOccurrenceAsync(hostId), await AddPlayerAsync(), RosterAssignmentStatus.Departed);

        var act = async () => await AddAssignmentAsync(occurrenceId, await AddPlayerAsync(), RosterAssignmentStatus.Confirmed, replacedAssignmentId: foreign);

        var sql = (await act.Should().ThrowAsync<DbUpdateException>()).Which.InnerException.Should().BeOfType<SqlException>().Which;
        sql.Number.Should().Be(ConstraintViolation);
        sql.Message.Should().Contain(RosterAssignmentConfiguration.ReplacedAssignmentForeignKeyName);
    }

    [Fact]
    public async Task StatusCheck_RejectsUndefinedStatus()
    {
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());

        var act = async () => await AddAssignmentAsync(occurrenceId, await AddPlayerAsync(), (RosterAssignmentStatus)8);

        (await act.Should().ThrowAsync<DbUpdateException>()).Which.InnerException.Should().BeOfType<SqlException>()
            .Which.Message.Should().Contain(RosterAssignmentConfiguration.StatusRangeCheckName);
    }

    // ── lifecycle of related rows ────────────────────────────────────────────

    [Fact]
    public async Task DeletingTheOccurrence_WithParticipationHistory_IsRefused_AndKeepsEveryRow()
    {
        var hostId = await AddPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);
        var requirementId = await AddRequirementAsync(occurrenceId, "guard", 2);
        var departed = await AddAssignmentAsync(occurrenceId, await AddPlayerAsync(), RosterAssignmentStatus.Departed, requirementId: requirementId);
        await AddAssignmentAsync(occurrenceId, await AddPlayerAsync(), RosterAssignmentStatus.Active, requirementId: requirementId, replacedAssignmentId: departed);

        var act = () => new EventService(_db.CreateContext()).DeleteAsync(occurrenceId);

        (await act.Should().ThrowAsync<RosterConflictException>()).Which.Code.Should().Be(RosterConflictCodes.OccurrenceHasParticipationHistory);
        await using var verify = _db.CreateContext();
        (await verify.Events.CountAsync(e => e.Id == occurrenceId)).Should().Be(1);
        (await verify.RosterRequirements.CountAsync(r => r.OccurrenceId == occurrenceId)).Should().Be(1);
        (await verify.RosterAssignments.CountAsync(a => a.OccurrenceId == occurrenceId)).Should().Be(2);
    }

    [Fact]
    public async Task DeletingTheOccurrence_WithOnlyHistoricalRows_IsStillRefused()
    {
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());
        await AddAssignmentAsync(occurrenceId, await AddPlayerAsync(), RosterAssignmentStatus.NoShow);

        var act = () => new EventService(_db.CreateContext()).DeleteAsync(occurrenceId);

        (await act.Should().ThrowAsync<RosterConflictException>()).Which.Code.Should().Be(RosterConflictCodes.OccurrenceHasParticipationHistory);
        await using var verify = _db.CreateContext();
        (await verify.RosterAssignments.CountAsync(a => a.OccurrenceId == occurrenceId)).Should().Be(1);
    }

    [Fact]
    public async Task DeletingTheOccurrence_WithoutAssignments_RemovesItAndItsRequirements()
    {
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());
        await AddRequirementAsync(occurrenceId, "guard", 2);

        (await new EventService(_db.CreateContext()).DeleteAsync(occurrenceId)).Should().BeTrue();

        await using var verify = _db.CreateContext();
        (await verify.Events.CountAsync(e => e.Id == occurrenceId)).Should().Be(0);
        (await verify.RosterRequirements.CountAsync(r => r.OccurrenceId == occurrenceId)).Should().Be(0);
    }

    [Fact]
    public async Task DeletingTheOccurrence_WhileAClaimCommits_IsAnOrderly409_AndTheClaimSurvives()
    {
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        var claimant = await AddPlayerAsync();

        // The claim commits after DeleteAsync found no history but before its DELETE runs: the
        // NO ACTION foreign key refuses the DELETE.
        await using var context = InterferingContext(() => ClaimViaServiceAsync(occurrenceId, claimant, requirementId));
        var act = () => new EventService(context).DeleteAsync(occurrenceId);

        (await act.Should().ThrowAsync<RosterConflictException>())
            .Which.Should().Match<RosterConflictException>(e =>
                e.Code == RosterConflictCodes.OccurrenceHasParticipationHistory && e.InnerException is DbUpdateException);
        await using var verify = _db.CreateContext();
        (await verify.Events.CountAsync(e => e.Id == occurrenceId)).Should().Be(1);
        (await verify.RosterAssignments.CountAsync(a => a.OccurrenceId == occurrenceId && a.PlayerId == claimant)).Should().Be(1);
    }

    // ── player schedule ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(2)]
    [InlineData(12)]
    public async Task PlayerOccurrences_UseFourQueriesPerPage_WhateverItsSize_AndNoPrivateColumns(int occurrences)
    {
        var hostId = await AddPlayerAsync();
        var playerId = await AddPlayerAsync();
        for (var i = 0; i < occurrences; i++)
        {
            var occurrenceId = await AddOccurrenceAsync(hostId);
            var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
            await ClaimViaServiceAsync(occurrenceId, playerId, requirementId);
            await ClaimViaServiceAsync(occurrenceId, await AddPlayerAsync(), requirementId);
        }

        var recorder = new CommandRecorder();
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseSqlServer(_db.ConnectionString)
            .AddInterceptors(recorder)
            .Options;
        await using var context = new TeamBuilderDbContext(options);

        var page = await new EventRosterService(context, TimeProvider.System).GetPlayerOccurrencesAsync(
            playerId,
            new PlayerOccurrenceQuery(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), null, IncludeTerminal: false, PageSize: 100, Cursor: null));

        page.Items.Should().HaveCount(occurrences).And.OnlyContain(i => i.SupplyCount == 2 && i.OpenQuantity == 8);
        recorder.Commands.Should().HaveCount(4, "the page, my assignments, requirements and supply rows: no N+1");
        recorder.Commands.Should().NotContain(c => c.Contains("[Email]") || c.Contains("PlayerIdentities"));
    }

    // ── host lifecycle interleavings ─────────────────────────────────────────

    [Fact]
    public async Task CheckIn_WhileThePlayerLeaves_IsAssignmentEnded_AndTheLeaveStands()
    {
        var hostId = await AddPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        var playerId = await AddPlayerAsync();
        var assignmentId = await ClaimViaServiceAsync(occurrenceId, playerId, requirementId);

        await using var context = InterferingContext(async () =>
        {
            await using var leaveContext = _db.CreateContext();
            await new EventRosterService(leaveContext, TimeProvider.System).LeaveAsync(occurrenceId, assignmentId, playerId);
        });
        var act = () => new EventRosterService(context, TimeProvider.System)
            .TransitionAsync(occurrenceId, assignmentId, hostId, RosterAssignmentTransition.CheckIn);

        (await act.Should().ThrowAsync<RosterConflictException>()).Which.Code.Should().Be(RosterConflictCodes.AssignmentEnded);
        var row = await RowAsync(assignmentId);
        (row.Status, row.ExitReason, row.CheckedInAtUtc).Should().Be((RosterAssignmentStatus.Cancelled, RosterExitReason.PlayerLeft, (DateTime?)null));
        (await SupplyOfRequirementAsync(requirementId)).Should().Be(0);
    }

    [Fact]
    public async Task NoShow_WhileThePlayerLeaves_ReleasesTheSpotExactlyOnce()
    {
        var hostId = await AddPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 2);
        var playerId = await AddPlayerAsync();
        var assignmentId = await ClaimViaServiceAsync(occurrenceId, playerId, requirementId);
        await ClaimViaServiceAsync(occurrenceId, await AddPlayerAsync(), requirementId);

        await using var context = InterferingContext(async () =>
        {
            await using var leaveContext = _db.CreateContext();
            await new EventRosterService(leaveContext, TimeProvider.System).LeaveAsync(occurrenceId, assignmentId, playerId);
        });
        var act = () => new EventRosterService(context, TimeProvider.System)
            .TransitionAsync(occurrenceId, assignmentId, hostId, RosterAssignmentTransition.NoShow);

        (await act.Should().ThrowAsync<RosterConflictException>()).Which.Code.Should().Be(RosterConflictCodes.AssignmentEnded);
        (await SupplyOfRequirementAsync(requirementId)).Should().Be(1);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [RosterAssignments] WHERE [OccurrenceId] = '{occurrenceId}'")).Should().Be(2);
    }

    [Fact]
    public async Task CheckIn_WhileTheHostTransfersTheEvent_IsRefusedForTheFormerHost()
    {
        var hostId = await AddPlayerAsync();
        var newHostId = await AddLinkedPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        var assignmentId = await ClaimViaServiceAsync(occurrenceId, await AddPlayerAsync(), requirementId);

        // The old host passed every check, then the transfer committed before the check-in saved.
        await using var context = InterferingContext(async () =>
        {
            await using var transferContext = _db.CreateContext();
            await new EventService(transferContext).TransferHostAsync(occurrenceId, hostId, newHostId);
        });
        var act = () => new EventRosterService(context, TimeProvider.System)
            .TransitionAsync(occurrenceId, assignmentId, hostId, RosterAssignmentTransition.CheckIn);

        await act.Should().ThrowAsync<OccurrenceHostForbiddenException>();
        (await RowAsync(assignmentId)).Status.Should().Be(RosterAssignmentStatus.Confirmed);

        // The new host can do it at once.
        var checkedIn = await new EventRosterService(_db.CreateContext(), TimeProvider.System)
            .TransitionAsync(occurrenceId, assignmentId, newHostId, RosterAssignmentTransition.CheckIn);
        checkedIn.Status.Should().Be(RosterAssignmentStatus.CheckedIn);
    }

    [Fact]
    public async Task Remove_WhileTheHostTransfersTheEvent_IsRefusedForTheFormerHost()
    {
        var hostId = await AddPlayerAsync();
        var newHostId = await AddLinkedPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        var assignmentId = await ClaimViaServiceAsync(occurrenceId, await AddPlayerAsync(), requirementId);

        await using var context = InterferingContext(async () =>
        {
            await using var transferContext = _db.CreateContext();
            await new EventService(transferContext).TransferHostAsync(occurrenceId, hostId, newHostId);
        });
        var act = () => new EventRosterService(context, TimeProvider.System).RemoveAsync(occurrenceId, assignmentId, hostId);

        await act.Should().ThrowAsync<OccurrenceHostForbiddenException>();
        (await RowAsync(assignmentId)).Status.Should().Be(RosterAssignmentStatus.Confirmed);
    }

    [Fact]
    public async Task CheckIn_WhileTheOccurrenceIsCancelled_IsOccurrenceClosed()
    {
        var hostId = await AddPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        var assignmentId = await ClaimViaServiceAsync(occurrenceId, await AddPlayerAsync(), requirementId);

        await using var context = InterferingContext(async () =>
        {
            await using var editContext = _db.CreateContext();
            await new EventService(editContext).UpdateAsync(occurrenceId, new UpdateEventDto { Status = EventStatus.Cancelled });
        });
        var act = () => new EventRosterService(context, TimeProvider.System)
            .TransitionAsync(occurrenceId, assignmentId, hostId, RosterAssignmentTransition.CheckIn);

        (await act.Should().ThrowAsync<RosterConflictException>()).Which.Code.Should().Be(RosterConflictCodes.OccurrenceClosed);
        (await RowAsync(assignmentId)).Status.Should().Be(RosterAssignmentStatus.Confirmed);
    }

    [Fact]
    public async Task CheckIn_WhileTheHostChecksInSomeoneElse_StillCommits()
    {
        // A sibling action moves the occurrence RowVersion; the re-validated retry succeeds.
        var hostId = await AddPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        var first = await ClaimViaServiceAsync(occurrenceId, await AddPlayerAsync(), requirementId);
        var second = await ClaimViaServiceAsync(occurrenceId, await AddPlayerAsync(), requirementId);

        await using var context = InterferingContext(async () =>
        {
            await using var siblingContext = _db.CreateContext();
            await new EventRosterService(siblingContext, TimeProvider.System)
                .TransitionAsync(occurrenceId, second, hostId, RosterAssignmentTransition.CheckIn);
        });
        var result = await new EventRosterService(context, TimeProvider.System)
            .TransitionAsync(occurrenceId, first, hostId, RosterAssignmentTransition.CheckIn);

        result.Status.Should().Be(RosterAssignmentStatus.CheckedIn);
        (await RowAsync(first)).Status.Should().Be(RosterAssignmentStatus.CheckedIn);
        (await RowAsync(second)).Status.Should().Be(RosterAssignmentStatus.CheckedIn);
        (await SupplyOfRequirementAsync(requirementId)).Should().Be(2);
    }

    [Fact]
    public async Task HostTransfer_WhileAnotherTransferCommits_IsOccurrenceChanged_AndTheFirstWins()
    {
        var hostId = await AddPlayerAsync();
        var firstTarget = await AddLinkedPlayerAsync();
        var secondTarget = await AddLinkedPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);

        await using var context = InterferingContext(async () =>
        {
            await using var transferContext = _db.CreateContext();
            await new EventService(transferContext).TransferHostAsync(occurrenceId, hostId, firstTarget);
        });
        var act = () => new EventService(context).TransferHostAsync(occurrenceId, hostId, secondTarget);

        (await act.Should().ThrowAsync<RosterConflictException>()).Which.Code.Should().Be(RosterConflictCodes.OccurrenceChanged);
        await using var verify = _db.CreateContext();
        (await verify.Events.SingleAsync(e => e.Id == occurrenceId)).HostId.Should().Be(firstTarget);
    }

    [Fact]
    public async Task DeletingAPlayerWithRosterHistory_IsRejected_AndKeepsTheHistory()
    {
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());
        var playerId = await AddPlayerAsync();
        await AddAssignmentAsync(occurrenceId, playerId, RosterAssignmentStatus.Departed);

        var act = () => new PlayerService(_db.CreateContext()).DeleteAsync(playerId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*roster history*");
        await using var verify = _db.CreateContext();
        (await verify.Players.AnyAsync(p => p.Id == playerId)).Should().BeTrue();
        (await verify.RosterAssignments.CountAsync(a => a.PlayerId == playerId)).Should().Be(1);
    }

    [Fact]
    public async Task DeletingAPlayer_WhileAnAssignmentAppears_IsAnOrderly409()
    {
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());
        var playerId = await AddPlayerAsync();

        await using var context = InterferingContext(() => AddAssignmentAsync(occurrenceId, playerId, RosterAssignmentStatus.Confirmed));
        var act = () => new PlayerService(context).DeleteAsync(playerId);

        (await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*roster history*"))
            .Which.InnerException.Should().BeOfType<DbUpdateException>();
        await using var verify = _db.CreateContext();
        (await verify.Players.AnyAsync(p => p.Id == playerId)).Should().BeTrue();
    }

    // ── commit-time host authority: requirement creation and host assignment ─

    [Fact]
    public async Task CreateRequirement_WhileTheHostTransfersTheEvent_IsRefusedForTheFormerHost()
    {
        var hostId = await AddPlayerAsync();
        var newHostId = await AddLinkedPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);

        // The old host passed every check, then the transfer committed before the save.
        await using var context = InterferingContext(() => TransferAsync(occurrenceId, hostId, newHostId));
        var act = () => new EventRosterService(context, TimeProvider.System)
            .CreateRequirementAsync(occurrenceId, new CreateRosterRequirementDto { RequiredCount = 10 }, hostId);

        await act.Should().ThrowAsync<OccurrenceHostForbiddenException>();
        (await RequirementCountAsync(occurrenceId)).Should().Be(0);

        // The new host can do it at once.
        var created = await new EventRosterService(_db.CreateContext(), TimeProvider.System)
            .CreateRequirementAsync(occurrenceId, new CreateRosterRequirementDto { RequiredCount = 10 }, newHostId);
        created.RequiredCount.Should().Be(10);
    }

    [Fact]
    public async Task HostAssignment_WhileTheHostTransfersTheEvent_IsRefusedForTheFormerHost()
    {
        var hostId = await AddPlayerAsync();
        var newHostId = await AddLinkedPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        var playerId = await AddPlayerAsync();

        await using var context = InterferingContext(() => TransferAsync(occurrenceId, hostId, newHostId));
        var act = () => new EventRosterService(context, TimeProvider.System).CreateAssignmentAsync(
            occurrenceId, new CreateRosterAssignmentDto { PlayerId = playerId, RequirementId = requirementId }, hostId);

        await act.Should().ThrowAsync<OccurrenceHostForbiddenException>();
        (await SupplyOfRequirementAsync(requirementId)).Should().Be(0);

        var assigned = await new EventRosterService(_db.CreateContext(), TimeProvider.System).CreateAssignmentAsync(
            occurrenceId, new CreateRosterAssignmentDto { PlayerId = playerId, RequirementId = requirementId }, newHostId);
        assigned.Source.Should().Be(RosterAssignmentSource.Host);
        (await SupplyOfRequirementAsync(requirementId)).Should().Be(1);
    }

    [Fact]
    public async Task TransferCommittedBeforeEachWrite_TheFormerHostCannotMutateTheRoster()
    {
        var hostId = await AddPlayerAsync();
        var newHostId = await AddLinkedPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        var assignmentId = await ClaimViaServiceAsync(occurrenceId, await AddPlayerAsync(), requirementId);
        await TransferAsync(occurrenceId, hostId, newHostId);

        var service = new EventRosterService(_db.CreateContext(), TimeProvider.System);
        var writes = new Func<Task>[]
        {
            () => service.CreateRequirementAsync(occurrenceId, new CreateRosterRequirementDto { RoleCode = "referee", RequiredCount = 1 }, hostId),
            () => service.CreateAssignmentAsync(occurrenceId, new CreateRosterAssignmentDto { PlayerId = Guid.NewGuid(), RequirementId = requirementId }, hostId),
            () => service.TransitionAsync(occurrenceId, assignmentId, hostId, RosterAssignmentTransition.CheckIn),
            () => service.TransitionAsync(occurrenceId, assignmentId, hostId, RosterAssignmentTransition.NoShow),
            () => service.RemoveAsync(occurrenceId, assignmentId, hostId),
            () => new EventService(_db.CreateContext()).TransferHostAsync(occurrenceId, hostId, hostId)
        };

        foreach (var write in writes)
            await write.Should().ThrowAsync<OccurrenceHostForbiddenException>();

        (await RequirementCountAsync(occurrenceId)).Should().Be(1);
        (await RowAsync(assignmentId)).Status.Should().Be(RosterAssignmentStatus.Confirmed);
        (await SupplyOfRequirementAsync(requirementId)).Should().Be(1);
    }

    [Fact]
    public async Task CreateRequirement_WhileTheOccurrenceIsCancelled_IsOccurrenceClosed()
    {
        var hostId = await AddPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);

        await using var context = InterferingContext(() => CancelAsync(occurrenceId));
        var act = () => new EventRosterService(context, TimeProvider.System)
            .CreateRequirementAsync(occurrenceId, new CreateRosterRequirementDto { RequiredCount = 10 }, hostId);

        (await act.Should().ThrowAsync<RosterConflictException>()).Which.Code.Should().Be(RosterConflictCodes.OccurrenceClosed);
        (await RequirementCountAsync(occurrenceId)).Should().Be(0);
    }

    [Fact]
    public async Task HostAssignment_WhileTheOccurrenceIsCancelled_IsOccurrenceClosed()
    {
        var hostId = await AddPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);

        await using var context = InterferingContext(() => CancelAsync(occurrenceId));
        var act = async () => await new EventRosterService(context, TimeProvider.System).CreateAssignmentAsync(
            occurrenceId, new CreateRosterAssignmentDto { PlayerId = await AddPlayerAsync(), RequirementId = requirementId }, hostId);

        (await act.Should().ThrowAsync<RosterConflictException>()).Which.Code.Should().Be(RosterConflictCodes.OccurrenceClosed);
        (await SupplyOfRequirementAsync(requirementId)).Should().Be(0);
    }

    [Fact]
    public async Task CreateRequirement_OnAnOrphanedOccurrence_IsOccurrenceHasNoHost()
    {
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());
        await ExecuteAsync($"UPDATE [Events] SET [HostId] = NULL WHERE [Id] = '{occurrenceId}'");

        var act = () => new EventRosterService(_db.CreateContext(), TimeProvider.System)
            .CreateRequirementAsync(occurrenceId, new CreateRosterRequirementDto { RequiredCount = 10 }, Guid.NewGuid());

        (await act.Should().ThrowAsync<RosterConflictException>()).Which.Code.Should().Be(RosterConflictCodes.OccurrenceHasNoHost);
    }

    [Fact]
    public async Task HostAssignment_LosingEveryRevalidation_IsABoundedRosterChanged()
    {
        // Every save is pre-empted by a sibling host action on the same occurrence: the write
        // re-validates a bounded number of times, then reports the retryable 409.
        var hostId = await AddPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        var saves = 0;

        await using var context = InterferingContext(
            async () =>
            {
                saves++;
                await ExecuteAsync($"UPDATE [Events] SET [UpdatedAtUtc] = SYSUTCDATETIME() WHERE [Id] = '{occurrenceId}'");
            },
            everySave: true);
        var act = async () => await new EventRosterService(context, TimeProvider.System).CreateAssignmentAsync(
            occurrenceId, new CreateRosterAssignmentDto { PlayerId = await AddPlayerAsync(), RequirementId = requirementId }, hostId);

        (await act.Should().ThrowAsync<RosterConflictException>()).Which.Code.Should().Be(RosterConflictCodes.RosterChanged);
        saves.Should().Be(EventRosterService.MaxHostUpdateAttempts);
        (await SupplyOfRequirementAsync(requirementId)).Should().Be(0);
    }

    // ── requirement-bound host assignment ────────────────────────────────────

    [Fact]
    public async Task HostAssignment_WithoutARequirement_IsRejected_AndWritesNothing()
    {
        var hostId = await AddPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);

        var act = async () => await new EventRosterService(_db.CreateContext(), TimeProvider.System).CreateAssignmentAsync(
            occurrenceId, new CreateRosterAssignmentDto { PlayerId = await AddPlayerAsync() }, hostId);

        (await act.Should().ThrowAsync<RosterValidationException>()).Which.Code.Should().Be(RosterValidationCodes.RequirementIdRequired);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [RosterAssignments] WHERE [OccurrenceId] = '{occurrenceId}'")).Should().Be(0);
    }

    [Fact]
    public async Task HostAssignment_ToARequirementOfAnotherOccurrence_IsRejected_AndWritesNothing()
    {
        var hostId = await AddPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);
        await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        var otherRequirement = await AddRequirementAsync(await AddOccurrenceAsync(hostId), RosterRoleCodesParticipant, 10);

        var act = async () => await new EventRosterService(_db.CreateContext(), TimeProvider.System).CreateAssignmentAsync(
            occurrenceId, new CreateRosterAssignmentDto { PlayerId = await AddPlayerAsync(), RequirementId = otherRequirement }, hostId);

        (await act.Should().ThrowAsync<RosterValidationException>()).Which.Code.Should().Be(RosterValidationCodes.RequirementNotOnOccurrence);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM [RosterAssignments]")).Should().Be(0);
    }

    // ── atomic event + initial roster ────────────────────────────────────────

    [Fact]
    public async Task CreateEvent_WithRequirementsAndPlayingHost_CommitsEverythingTogether()
    {
        var hostId = await AddPlayerAsync();

        var created = await new EventService(_db.CreateContext()).CreateAsync(
            new CreateEventDto
            {
                Name = "Wednesday 8 PM pickup basketball",
                EventDateUtc = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc),
                ScheduledEndUtc = new DateTime(2026, 10, 15, 2, 0, 0, DateTimeKind.Utc),
                RosterRequirements =
                [
                    new CreateRosterRequirementDto { RoleCode = "Guard", RequiredCount = 4 },
                    new CreateRosterRequirementDto { RoleCode = "forward", RequiredCount = 6 }
                ],
                HostParticipates = true,
                HostRoleCode = "forward"
            },
            hostId);

        var summary = (await new EventRosterService(_db.CreateContext(), TimeProvider.System).GetSummaryAsync(created.Id))!;
        summary.Requirements.Select(r => (r.RoleCode, r.RequiredCount, r.SupplyCount)).Should().Equal(("guard", 4, 0), ("forward", 6, 1));
        (summary.RequiredCount, summary.SupplyCount, summary.OpenQuantity, summary.IsRosterReady).Should().Be((10, 1, 9, false));
        var hostRow = summary.Assignments.Should().ContainSingle().Subject;
        (hostRow.PlayerId, hostRow.Status, hostRow.Source).Should().Be((hostId, RosterAssignmentStatus.Confirmed, RosterAssignmentSource.Player));
        hostRow.RequirementId.Should().Be(summary.Requirements.Single(r => r.RoleCode == "forward").Id);
        created.ScheduledEndUtc.Should().Be(new DateTime(2026, 10, 15, 2, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task CreateEvent_OrganizerOnlyHostIsZeroOfN_PlayingHostIsOneOfN(bool hostParticipates, int expectedSupply)
    {
        var hostId = await AddPlayerAsync();

        var created = await new EventService(_db.CreateContext()).CreateAsync(
            new CreateEventDto
            {
                Name = "Run",
                EventDateUtc = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc),
                RosterRequirements = [new CreateRosterRequirementDto { RoleCode = "participant", RequiredCount = 10 }],
                HostParticipates = hostParticipates
            },
            hostId);

        var summary = (await new EventRosterService(_db.CreateContext(), TimeProvider.System).GetSummaryAsync(created.Id))!;
        (summary.RequiredCount, summary.SupplyCount, summary.OpenQuantity).Should().Be((10, expectedSupply, 10 - expectedSupply));
        created.HostId.Should().Be(hostId);
        summary.Assignments.Select(a => a.PlayerId).Should().Equal(hostParticipates ? [hostId] : Array.Empty<Guid>());
    }

    [Fact]
    public async Task CreateEvent_WhenARequirementCannotBeSaved_RollsBackTheWholeCreate()
    {
        // RequiredCount 0 bypasses DTO validation here and is refused by the database CHECK in
        // the same SaveChanges as the occurrence, its other requirement and the host's spot.
        var hostId = await AddPlayerAsync();

        var act = () => new EventService(_db.CreateContext()).CreateAsync(
            new CreateEventDto
            {
                Name = "Doomed run",
                EventDateUtc = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc),
                RosterRequirements =
                [
                    new CreateRosterRequirementDto { RoleCode = "participant", RequiredCount = 10 },
                    new CreateRosterRequirementDto { RoleCode = "referee", RequiredCount = 0 }
                ],
                HostParticipates = true,
                HostRoleCode = "participant"
            },
            hostId);

        (await act.Should().ThrowAsync<DbUpdateException>()).Which.InnerException.Should().BeOfType<SqlException>()
            .Which.Number.Should().Be(ConstraintViolation);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM [Events] WHERE [Name] = N'Doomed run'")).Should().Be(0);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM [RosterRequirements]")).Should().Be(0);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM [RosterAssignments]")).Should().Be(0);
    }

    [Fact]
    public async Task CreateEvent_WithoutRosterFields_KeepsTheExistingBehavior()
    {
        var created = await new EventService(_db.CreateContext()).CreateAsync(
            new CreateEventDto { Name = "Social", EventDateUtc = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc) },
            await AddPlayerAsync());

        (await RequirementCountAsync(created.Id)).Should().Be(0);
        created.ScheduledEndUtc.Should().BeNull();
    }

    // ── my hosted games ──────────────────────────────────────────────────────

    [Fact]
    public async Task PlayerOccurrences_IncludeHosted_ListsOrganizerOnlyGamesOnce_AndKeepsTheDefault()
    {
        var hostId = await AddPlayerAsync();
        var organizing = await AddOccurrenceAsync(hostId);
        var organizingRequirement = await AddRequirementAsync(organizing, RosterRoleCodesParticipant, 10);
        await ClaimViaServiceAsync(organizing, await AddPlayerAsync(), organizingRequirement);
        var playing = await AddOccurrenceAsync(hostId);
        await ClaimViaServiceAsync(playing, hostId, await AddRequirementAsync(playing, RosterRoleCodesParticipant, 10));
        var from = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        var defaultPage = await new EventRosterService(_db.CreateContext(), TimeProvider.System)
            .GetPlayerOccurrencesAsync(hostId, new PlayerOccurrenceQuery(from, null, false, 20, null));
        defaultPage.Items.Select(i => i.OccurrenceId).Should().Equal(playing);

        var recorder = new CommandRecorder();
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>().UseSqlServer(_db.ConnectionString).AddInterceptors(recorder).Options;
        await using var context = new TeamBuilderDbContext(options);
        var hostedPage = await new EventRosterService(context, TimeProvider.System)
            .GetPlayerOccurrencesAsync(hostId, new PlayerOccurrenceQuery(from, null, false, 20, null, IncludeHosted: true));

        hostedPage.Items.Select(i => i.OccurrenceId).Should().BeEquivalentTo([organizing, playing]);
        var organizerOnly = hostedPage.Items.Single(i => i.OccurrenceId == organizing);
        (organizerOnly.IsHost, organizerOnly.MyAssignmentId, organizerOnly.MyAssignmentStatus).Should().Be((true, (Guid?)null, (RosterAssignmentStatus?)null));
        (organizerOnly.TotalRequiredCount, organizerOnly.TotalSupplyCount, organizerOnly.TotalOpenQuantity).Should().Be((10, 1, 9));
        organizerOnly.RequiredCount.Should().BeNull();
        var both = hostedPage.Items.Single(i => i.OccurrenceId == playing);
        (both.IsHost, both.MyAssignmentStatus, both.SupplyCount).Should().Be((true, RosterAssignmentStatus.Confirmed, 1));
        recorder.Commands.Should().HaveCount(4, "includeHosted keeps the fixed four queries per page");
        recorder.Commands.Should().NotContain(c => c.Contains("[Email]") || c.Contains("PlayerIdentities"));
    }

    [Fact]
    public async Task PlayerOccurrences_IncludeHosted_PagesDeterministicallyAcrossHostedAndPlayedGames()
    {
        var hostId = await AddPlayerAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var id = await AddOccurrenceAsync(hostId);
            if (i % 2 == 0)
                await ClaimViaServiceAsync(id, hostId, await AddRequirementAsync(id, RosterRoleCodesParticipant, 10));
            ids.Add(id);
        }

        var service = new EventRosterService(_db.CreateContext(), TimeProvider.System);
        var from = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await service.GetPlayerOccurrencesAsync(hostId, new PlayerOccurrenceQuery(from, null, false, 2, cursor, IncludeHosted: true));
            seen.AddRange(page.Items.Select(i => i.OccurrenceId));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        // Every occurrence starts at the same instant: the Id tiebreak (SQL Server
        // uniqueidentifier order) makes the paged walk match one unpaged read, with no gaps or
        // repeats across hosted-only and played games.
        var unpaged = await service.GetPlayerOccurrencesAsync(hostId, new PlayerOccurrenceQuery(from, null, false, 100, null, IncludeHosted: true));
        seen.Should().Equal(unpaged.Items.Select(i => i.OccurrenceId));
        seen.Should().BeEquivalentTo(ids);
    }

    // ── host transfer lifecycle ──────────────────────────────────────────────

    [Theory]
    [InlineData(EventStatus.Completed)]
    [InlineData(EventStatus.Cancelled)]
    [InlineData(EventStatus.Archived)]
    public async Task HostTransfer_OfAClosedOccurrence_IsOccurrenceClosed_AndKeepsTheHost(EventStatus status)
    {
        var hostId = await AddPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId, status);

        var act = async () => await new EventService(_db.CreateContext()).TransferHostAsync(occurrenceId, hostId, await AddLinkedPlayerAsync());

        (await act.Should().ThrowAsync<RosterConflictException>()).Which.Code.Should().Be(RosterConflictCodes.OccurrenceClosed);
        await using var verify = _db.CreateContext();
        (await verify.Events.SingleAsync(e => e.Id == occurrenceId)).HostId.Should().Be(hostId);
    }

    [Theory]
    [InlineData(EventStatus.Planned)]
    [InlineData(EventStatus.Open)]
    [InlineData(EventStatus.InProgress)]
    public async Task HostTransfer_OfALiveOccurrence_IsAllowed(EventStatus status)
    {
        var hostId = await AddPlayerAsync();
        var newHostId = await AddLinkedPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId, status);

        (await new EventService(_db.CreateContext()).TransferHostAsync(occurrenceId, hostId, newHostId)).HostId.Should().Be(newHostId);
    }

    [Fact]
    public async Task HostTransfer_WhileTheOccurrenceIsCancelled_IsOccurrenceChanged_AndTheCancellationStands()
    {
        var hostId = await AddPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);

        await using var context = InterferingContext(() => CancelAsync(occurrenceId));
        var act = async () => await new EventService(context).TransferHostAsync(occurrenceId, hostId, await AddLinkedPlayerAsync());

        (await act.Should().ThrowAsync<RosterConflictException>()).Which.Code.Should().Be(RosterConflictCodes.OccurrenceChanged);
        await using var verify = _db.CreateContext();
        var row = await verify.Events.SingleAsync(e => e.Id == occurrenceId);
        (row.HostId, row.Status).Should().Be((hostId, EventStatus.Cancelled));
    }

    // ── occurrence detail ────────────────────────────────────────────────────

    [Fact]
    public async Task OccurrenceDetail_ListsLiveParticipantsOnly_InThreeQueries_WithNoPrivateColumns()
    {
        var hostId = await AddPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        await ClaimViaServiceAsync(occurrenceId, hostId, requirementId);
        var callerId = await AddPlayerAsync();
        var mine = await ClaimViaServiceAsync(occurrenceId, callerId, requirementId);
        await AddAssignmentAsync(occurrenceId, await AddPlayerAsync(), RosterAssignmentStatus.NoShow, requirementId);

        var recorder = new CommandRecorder();
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>().UseSqlServer(_db.ConnectionString).AddInterceptors(recorder).Options;
        await using var context = new TeamBuilderDbContext(options);
        var detail = (await new EventRosterService(context, TimeProvider.System).GetOccurrenceDetailAsync(occurrenceId, callerId))!;

        (detail.RequiredCount, detail.SupplyCount, detail.OpenQuantity, detail.IsRosterReady).Should().Be((10, 2, 8, false));
        detail.Participants.Should().HaveCount(2).And.OnlyContain(p => p.Status == RosterAssignmentStatus.Confirmed);
        detail.Participants.Single(p => p.PlayerId == hostId).IsHost.Should().BeTrue();
        (detail.IsHost, detail.MyAssignmentId, detail.MyAssignmentStatus, detail.AcceptsRosterChanges)
            .Should().Be((false, (Guid?)mine, (RosterAssignmentStatus?)RosterAssignmentStatus.Confirmed, true));
        recorder.Commands.Should().HaveCount(3, "occurrence, requirements and live participants: no N+1");
        recorder.Commands.Should().NotContain(c => c.Contains("[Email]") || c.Contains("PlayerIdentities"));
    }

    private async Task TransferAsync(Guid occurrenceId, Guid fromHostId, Guid toHostId)
    {
        await using var transferContext = _db.CreateContext();
        await new EventService(transferContext).TransferHostAsync(occurrenceId, fromHostId, toHostId);
    }

    private async Task CancelAsync(Guid occurrenceId)
    {
        await using var editContext = _db.CreateContext();
        await new EventService(editContext).UpdateAsync(occurrenceId, new UpdateEventDto { Status = EventStatus.Cancelled });
    }

    private async Task<int> RequirementCountAsync(Guid occurrenceId) =>
        await ScalarAsync<int>($"SELECT COUNT(*) FROM [RosterRequirements] WHERE [OccurrenceId] = '{occurrenceId}'");

    // ── helpers ──────────────────────────────────────────────────────────────

    private const string RosterRoleCodesParticipant = RosterRoleCodes.Participant;

    /// <summary>Host assignment as the occurrence's current host (read from the database).</summary>
    private async Task<Guid> AssignViaServiceAsync(Guid occurrenceId, Guid playerId, Guid requirementId)
    {
        await using var context = _db.CreateContext();
        var hostId = await context.Events.Where(e => e.Id == occurrenceId).Select(e => e.HostId!.Value).SingleAsync();
        var assignment = await new EventRosterService(context, TimeProvider.System).CreateAssignmentAsync(
            occurrenceId,
            new CreateRosterAssignmentDto { PlayerId = playerId, RequirementId = requirementId },
            hostId);
        return assignment.Id;
    }

    private async Task<Guid> ClaimViaServiceAsync(Guid occurrenceId, Guid playerId, Guid requirementId)
    {
        await using var context = _db.CreateContext();
        var result = await new EventRosterService(context, TimeProvider.System)
            .ClaimAsync(occurrenceId, playerId, new ClaimRosterSpotDto { RequirementId = requirementId });
        return result.Assignment.Id;
    }

    private async Task<RosterAssignment> RowAsync(Guid assignmentId)
    {
        await using var context = _db.CreateContext();
        return await context.RosterAssignments.AsNoTracking().SingleAsync(a => a.Id == assignmentId);
    }

    private async Task<Guid> AddLinkedPlayerAsync()
    {
        var playerId = await AddPlayerAsync();
        await using var context = _db.CreateContext();
        context.PlayerIdentities.Add(new PlayerIdentity
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            Issuer = "https://issuer.test",
            Subject = $"ext|{Guid.NewGuid():N}",
            Provider = "oidc"
        });
        await context.SaveChangesAsync();
        return playerId;
    }

    private async Task<int> SupplyOfRequirementAsync(Guid requirementId) =>
        await ScalarAsync<int>($"SELECT COUNT(*) FROM [RosterAssignments] WHERE [RequirementId] = '{requirementId}' AND [Status] IN (1, 2, 3, 4)");

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private TeamBuilderDbContext InterferingContext(Func<Task> beforeFirstSave, bool everySave = false)
    {
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseSqlServer(_db.ConnectionString)
            .Options;
        return new InterferingTeamBuilderDbContext(options, beforeFirstSave, everySave);
    }

    private async Task<Guid> AddPlayerAsync()
    {
        await using var context = _db.CreateContext();
        var player = new Player { Id = Guid.NewGuid(), Username = $"p-{Guid.NewGuid():N}", Email = "private@example.com" };
        context.Players.Add(player);
        await context.SaveChangesAsync();
        return player.Id;
    }

    private async Task<Guid> AddOccurrenceAsync(Guid hostId, EventStatus status = EventStatus.Planned)
    {
        await using var context = _db.CreateContext();
        var occurrence = new EventOccurrence
        {
            Id = Guid.NewGuid(),
            Name = "Run",
            ScheduledStartUtc = new DateTime(2026, 10, 20, 22, 0, 0, DateTimeKind.Utc),
            Status = status,
            MaxParticipants = 10,
            HostId = hostId
        };
        context.Events.Add(occurrence);
        await context.SaveChangesAsync();
        return occurrence.Id;
    }

    private async Task<Guid> AddRequirementAsync(Guid occurrenceId, string roleCode, int requiredCount)
    {
        await using var context = _db.CreateContext();
        var requirement = new RosterRequirement
        {
            Id = Guid.NewGuid(),
            OccurrenceId = occurrenceId,
            RoleCode = roleCode,
            RequiredCount = requiredCount
        };
        context.RosterRequirements.Add(requirement);
        await context.SaveChangesAsync();
        return requirement.Id;
    }

    private async Task<Guid> AddAssignmentAsync(
        Guid occurrenceId,
        Guid playerId,
        RosterAssignmentStatus status,
        Guid? requirementId = null,
        Guid? replacedAssignmentId = null,
        Guid? id = null)
    {
        await using var context = _db.CreateContext();
        var assignment = new RosterAssignment
        {
            Id = id ?? Guid.NewGuid(),
            OccurrenceId = occurrenceId,
            PlayerId = playerId,
            RequirementId = requirementId,
            Status = status,
            Source = RosterAssignmentSource.Host,
            ReplacedAssignmentId = replacedAssignmentId
        };
        context.RosterAssignments.Add(assignment);
        await context.SaveChangesAsync();
        return assignment.Id;
    }

    private async Task<List<string>> ColumnsOfIndexAsync(string indexName)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT c.[name] FROM sys.index_columns ic
            JOIN sys.indexes i ON i.[object_id] = ic.[object_id] AND i.[index_id] = ic.[index_id]
            JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
            WHERE i.[name] = N'{indexName}'
            ORDER BY ic.[key_ordinal]
            """;
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));
        return columns;
    }

    private async Task<List<(string Parent, string Referenced)>> ForeignKeyColumnsAsync(string foreignKeyName)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT pc.[name], rc.[name] FROM sys.foreign_key_columns fkc
            JOIN sys.foreign_keys fk ON fk.[object_id] = fkc.[constraint_object_id]
            JOIN sys.columns pc ON pc.[object_id] = fkc.[parent_object_id] AND pc.[column_id] = fkc.[parent_column_id]
            JOIN sys.columns rc ON rc.[object_id] = fkc.[referenced_object_id] AND rc.[column_id] = fkc.[referenced_column_id]
            WHERE fk.[name] = N'{foreignKeyName}'
            ORDER BY fkc.[constraint_column_id]
            """;
        var columns = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add((reader.GetString(0), reader.GetString(1)));
        return columns;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private sealed class InterferingTeamBuilderDbContext(
        DbContextOptions<TeamBuilderDbContext> options,
        Func<Task> beforeFirstSave,
        bool everySave) : TeamBuilderDbContext(options)
    {
        private Func<Task>? _beforeFirstSave = beforeFirstSave;

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var interference = _beforeFirstSave;
            if (!everySave)
                _beforeFirstSave = null;
            if (interference is not null)
                await interference();

            return await base.SaveChangesAsync(cancellationToken);
        }
    }
}
