using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TeamBuilder.Application.DTOs;
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
/// CHECK, deterministic duplicate races, cascade on event delete and player-delete protection.
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

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "roster");
        await _db.MigrateToAsync();
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
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());

        await using var context = InterferingContext(() => AddRequirementAsync(occurrenceId, "healer", 3));
        var service = new EventRosterService(context, TimeProvider.System);

        var act = () => service.CreateRequirementAsync(occurrenceId, new CreateRosterRequirementDto { RoleCode = "healer", RequiredCount = 4 });

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
        var playerId = await AddPlayerAsync();

        // A competing request commits the same player's supply row after this request's
        // duplicate pre-check but before its INSERT: the filtered unique index decides.
        await using var context = InterferingContext(() => AddAssignmentAsync(occurrenceId, playerId, RosterAssignmentStatus.Reserved));
        var service = new EventRosterService(context, TimeProvider.System);

        var act = () => service.CreateAssignmentAsync(occurrenceId, new CreateRosterAssignmentDto { PlayerId = playerId }, RosterAssignmentSource.Host);

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
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());
        var playerId = await AddPlayerAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var context = _db.CreateContext();
            try
            {
                await new EventRosterService(context, TimeProvider.System)
                    .CreateAssignmentAsync(occurrenceId, new CreateRosterAssignmentDto { PlayerId = playerId }, RosterAssignmentSource.Host);
                return true;
            }
            catch (InvalidOperationException ex) when (ex.Message == EventRosterService.DuplicateSupplyAssignmentMessage)
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
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 10);
        for (var i = 0; i < 9; i++)
            await AssignViaServiceAsync(occurrenceId, await AddPlayerAsync(), requirementId);

        // Another host request takes the 10th spot after this request counted 9 but before it
        // commits: the forced RowVersion-checked requirement UPDATE makes this save fail.
        var competitor = await AddPlayerAsync();
        var playerId = await AddPlayerAsync();
        await using var context = InterferingContext(() => AssignViaServiceAsync(occurrenceId, competitor, requirementId));
        var service = new EventRosterService(context, TimeProvider.System);

        var act = () => service.CreateAssignmentAsync(
            occurrenceId,
            new CreateRosterAssignmentDto { PlayerId = playerId, RequirementId = requirementId },
            RosterAssignmentSource.Host);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage(EventRosterService.RequirementChangedMessage)
            .Which.InnerException.Should().BeOfType<DbUpdateConcurrencyException>();
        (await SupplyOfRequirementAsync(requirementId)).Should().Be(10);
    }

    [Fact]
    public async Task CreateAssignment_ParallelFills_NeverExceedRequiredCount()
    {
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());
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
                    RosterAssignmentSource.Host);
                return true;
            }
            catch (InvalidOperationException ex) when (
                ex.Message is EventRosterService.RequirementFilledMessage or EventRosterService.RequirementChangedMessage)
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
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());
        var requirementId = await AddRequirementAsync(occurrenceId, RosterRoleCodesParticipant, 1);
        var first = await AssignViaServiceAsync(occurrenceId, await AddPlayerAsync(), requirementId);
        await ExecuteAsync($"UPDATE [RosterAssignments] SET [Status] = 5, [ExitReason] = 1, [DepartedAtUtc] = SYSUTCDATETIME() WHERE [Id] = '{first}'");

        await new EventRosterService(_db.CreateContext(), TimeProvider.System).CreateAssignmentAsync(
            occurrenceId,
            new CreateRosterAssignmentDto { PlayerId = await AddPlayerAsync(), RequirementId = requirementId, ReplacedAssignmentId = first },
            RosterAssignmentSource.Host);

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
        var occurrenceId = await AddOccurrenceAsync(await AddPlayerAsync());
        var requirementId = await AddRequirementAsync(occurrenceId, "tank", 1);
        var departedPlayer = await AddPlayerAsync();
        var original = await AddAssignmentAsync(occurrenceId, departedPlayer, RosterAssignmentStatus.NoShow, requirementId: requirementId);
        byte[] rowVersionBefore;
        await using (var context = _db.CreateContext())
            rowVersionBefore = (await context.RosterAssignments.AsNoTracking().SingleAsync(a => a.Id == original)).RowVersion;

        var replacement = await new EventRosterService(_db.CreateContext(), TimeProvider.System).CreateAssignmentAsync(
            occurrenceId,
            new CreateRosterAssignmentDto { PlayerId = await AddPlayerAsync(), RequirementId = requirementId, ReplacedAssignmentId = original },
            RosterAssignmentSource.Host);

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
    public async Task DeletingTheOccurrence_RemovesItsRequirementsAndAssignmentHistory()
    {
        var hostId = await AddPlayerAsync();
        var occurrenceId = await AddOccurrenceAsync(hostId);
        var otherOccurrenceId = await AddOccurrenceAsync(hostId);
        var requirementId = await AddRequirementAsync(occurrenceId, "guard", 2);
        var departed = await AddAssignmentAsync(occurrenceId, await AddPlayerAsync(), RosterAssignmentStatus.Departed, requirementId: requirementId);
        await AddAssignmentAsync(occurrenceId, await AddPlayerAsync(), RosterAssignmentStatus.Active, requirementId: requirementId, replacedAssignmentId: departed);
        await AddAssignmentAsync(otherOccurrenceId, await AddPlayerAsync(), RosterAssignmentStatus.Active);

        (await new EventService(_db.CreateContext()).DeleteAsync(occurrenceId)).Should().BeTrue();

        await using var verify = _db.CreateContext();
        (await verify.RosterRequirements.CountAsync(r => r.OccurrenceId == occurrenceId)).Should().Be(0);
        (await verify.RosterAssignments.CountAsync(a => a.OccurrenceId == occurrenceId)).Should().Be(0);
        (await verify.RosterAssignments.CountAsync(a => a.OccurrenceId == otherOccurrenceId)).Should().Be(1);
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

    // ── helpers ──────────────────────────────────────────────────────────────

    private const string RosterRoleCodesParticipant = RosterRoleCodes.Participant;

    private async Task<Guid> AssignViaServiceAsync(Guid occurrenceId, Guid playerId, Guid requirementId)
    {
        await using var context = _db.CreateContext();
        var assignment = await new EventRosterService(context, TimeProvider.System).CreateAssignmentAsync(
            occurrenceId,
            new CreateRosterAssignmentDto { PlayerId = playerId, RequirementId = requirementId },
            RosterAssignmentSource.Host);
        return assignment.Id;
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

    private TeamBuilderDbContext InterferingContext(Func<Task> beforeFirstSave)
    {
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseSqlServer(_db.ConnectionString)
            .Options;
        return new InterferingTeamBuilderDbContext(options, beforeFirstSave);
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
