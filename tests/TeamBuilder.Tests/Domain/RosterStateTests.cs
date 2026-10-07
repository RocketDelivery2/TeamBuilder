using FluentAssertions;
using TeamBuilder.Domain;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Tests.Domain;

public class RosterStateTests
{
    [Theory]
    [InlineData(RosterAssignmentStatus.Reserved, true)]
    [InlineData(RosterAssignmentStatus.Confirmed, true)]
    [InlineData(RosterAssignmentStatus.CheckedIn, true)]
    [InlineData(RosterAssignmentStatus.Active, true)]
    [InlineData(RosterAssignmentStatus.Departed, false)]
    [InlineData(RosterAssignmentStatus.NoShow, false)]
    [InlineData(RosterAssignmentStatus.Cancelled, false)]
    public void IsSupply_CountsOnlyReservedConfirmedCheckedInActive(RosterAssignmentStatus status, bool expected)
    {
        RosterState.IsSupply(status).Should().Be(expected);
        RosterState.SupplyStatuses.Contains(status).Should().Be(expected);
    }

    [Fact]
    public void SupplyStatuses_HaveTheIntegerValuesUsedByTheSqlFilter()
    {
        RosterState.SupplyStatuses.Select(s => (int)s).Should().Equal(1, 2, 3, 4);
    }

    [Theory]
    [InlineData(5, 0, 5)]
    [InlineData(5, 3, 2)]
    [InlineData(5, 5, 0)]
    [InlineData(5, 7, 0)]
    [InlineData(1, 100, 0)]
    public void OpenQuantity_IsRequiredMinusSupply_NeverNegative(int required, int supply, int expected)
    {
        RosterState.OpenQuantity(required, supply).Should().Be(expected);
    }

    [Fact]
    public void Compute_CountsSupplyPerRequirement_AndUnlinkedSupplyOnlyOnTheOccurrence()
    {
        var guards = Guid.NewGuid();
        var centers = Guid.NewGuid();

        var occurrenceId = Guid.NewGuid();
        var snapshot = RosterState.Compute(
            occurrenceId,
            [(guards, 2), (centers, 1)],
            [
                (guards, RosterAssignmentStatus.Reserved),
                (guards, RosterAssignmentStatus.Confirmed),
                (guards, RosterAssignmentStatus.CheckedIn), // over-assigned: open stays 0
                (guards, RosterAssignmentStatus.Departed),
                (centers, RosterAssignmentStatus.NoShow),
                (centers, RosterAssignmentStatus.Cancelled),
                (null, RosterAssignmentStatus.Active),
                (null, RosterAssignmentStatus.Departed)
            ]);

        snapshot.OccurrenceId.Should().Be(occurrenceId);
        snapshot.TotalSupplyCount.Should().Be(4);
        snapshot.UnlinkedSupplyCount.Should().Be(1);
        snapshot.TotalRequiredCount.Should().Be(3);
        // Surplus guards never offset the open center.
        snapshot.TotalOpenQuantity.Should().Be(1);
        snapshot.IsRosterReady.Should().BeFalse();
        snapshot.Requirements[guards].Should().Be(new RequirementSupply(guards, 2, 3, 0));
        snapshot.Requirements[centers].Should().Be(new RequirementSupply(centers, 1, 0, 1));
    }

    [Theory]
    [InlineData(0, 10, false)]
    [InlineData(5, 5, false)]
    [InlineData(9, 1, false)]
    [InlineData(10, 0, true)]
    public void Basketball_TenParticipants_Readiness(int confirmed, int expectedOpen, bool expectedReady)
    {
        var participant = Guid.NewGuid();

        var snapshot = RosterState.Compute(
            Guid.NewGuid(),
            [(participant, 10)],
            Enumerable.Repeat<(Guid?, RosterAssignmentStatus)>((participant, RosterAssignmentStatus.Confirmed), confirmed)
                .Append((participant, RosterAssignmentStatus.Departed)));

        snapshot.TotalRequiredCount.Should().Be(10);
        snapshot.TotalSupplyCount.Should().Be(confirmed);
        snapshot.TotalOpenQuantity.Should().Be(expectedOpen);
        snapshot.IsRosterReady.Should().Be(expectedReady);
    }

    [Fact]
    public void NoRequirements_IsNeverReady_EvenWithSupply()
    {
        var snapshot = RosterState.Compute(Guid.NewGuid(), [], [(null, RosterAssignmentStatus.Active)]);

        snapshot.TotalRequiredCount.Should().Be(0);
        snapshot.TotalOpenQuantity.Should().Be(0);
        snapshot.TotalSupplyCount.Should().Be(1);
        snapshot.IsRosterReady.Should().BeFalse();
    }

    [Fact]
    public void MultipleRoles_ReadyOnlyWhenEveryRoleIsFilled()
    {
        var tank = Guid.NewGuid();
        var healer = Guid.NewGuid();

        RosterState.Compute(Guid.NewGuid(), [(tank, 1), (healer, 1)], [(tank, RosterAssignmentStatus.Active), (null, RosterAssignmentStatus.Active)])
            .IsRosterReady.Should().BeFalse();
        RosterState.Compute(Guid.NewGuid(), [(tank, 1), (healer, 1)], [(tank, RosterAssignmentStatus.Active), (healer, RosterAssignmentStatus.Reserved)])
            .IsRosterReady.Should().BeTrue();
    }

    [Theory]
    [InlineData(EventStatus.Planned, true)]
    [InlineData(EventStatus.Open, true)]
    [InlineData(EventStatus.InProgress, true)]
    [InlineData(EventStatus.Completed, false)]
    [InlineData(EventStatus.Cancelled, false)]
    [InlineData(EventStatus.Archived, false)]
    public void AcceptsNewRosterMutations_ClosesCompletedCancelledArchived(EventStatus status, bool expected)
    {
        RosterState.AcceptsNewRosterMutations(status).Should().Be(expected);
    }

    [Theory]
    [InlineData("participant", "participant")]
    [InlineData("  Guard ", "guard")]
    [InlineData("tank", "tank")]
    [InlineData("dps.melee", "dps.melee")]
    [InlineData("off-tank_2", "off-tank_2")]
    [InlineData("9man", "9man")]
    public void RoleCode_Normalizes(string input, string expected)
    {
        RosterRoleCodes.TryNormalize(input, out var normalized, out _).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-guard")]
    [InlineData("point guard")]
    [InlineData("guard!")]
    [InlineData("gärd")]
    public void RoleCode_RejectsMalformedCodes(string input)
    {
        RosterRoleCodes.TryNormalize(input, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void RoleCode_RejectsTooLongCodes()
    {
        RosterRoleCodes.TryNormalize(new string('a', RosterRoleCodes.MaxLength + 1), out _, out _).Should().BeFalse();
        RosterRoleCodes.TryNormalize(new string('a', RosterRoleCodes.MaxLength), out _, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData(RosterAssignmentStatus.Reserved, RosterAssignmentStatus.Cancelled)]
    [InlineData(RosterAssignmentStatus.Confirmed, RosterAssignmentStatus.Cancelled)]
    [InlineData(RosterAssignmentStatus.CheckedIn, RosterAssignmentStatus.Departed)]
    [InlineData(RosterAssignmentStatus.Active, RosterAssignmentStatus.Departed)]
    public void ExitStatusFor_LiveStatuses_EndOutsideSupply(RosterAssignmentStatus from, RosterAssignmentStatus expected)
    {
        var exit = RosterState.ExitStatusFor(from);

        exit.Should().Be(expected);
        RosterState.IsSupply(exit).Should().BeFalse();
    }

    [Theory]
    [InlineData(RosterAssignmentStatus.Departed)]
    [InlineData(RosterAssignmentStatus.NoShow)]
    [InlineData(RosterAssignmentStatus.Cancelled)]
    public void ExitStatusFor_HistoricalStatuses_Throws(RosterAssignmentStatus from)
    {
        var act = () => RosterState.ExitStatusFor(from);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TargetStatusFor_AllowsExactlyTheHostLifecycleSteps()
    {
        var allowed = new Dictionary<(RosterAssignmentStatus, RosterAssignmentTransition), RosterAssignmentStatus>
        {
            [(RosterAssignmentStatus.Confirmed, RosterAssignmentTransition.CheckIn)] = RosterAssignmentStatus.CheckedIn,
            [(RosterAssignmentStatus.CheckedIn, RosterAssignmentTransition.Activate)] = RosterAssignmentStatus.Active,
            [(RosterAssignmentStatus.Confirmed, RosterAssignmentTransition.NoShow)] = RosterAssignmentStatus.NoShow,
            [(RosterAssignmentStatus.CheckedIn, RosterAssignmentTransition.NoShow)] = RosterAssignmentStatus.NoShow
        };

        foreach (var status in Enum.GetValues<RosterAssignmentStatus>())
        {
            foreach (var transition in Enum.GetValues<RosterAssignmentTransition>())
            {
                var target = RosterState.TargetStatusFor(status, transition);
                if (allowed.TryGetValue((status, transition), out var expected))
                    target.Should().Be(expected, $"{transition} from {status}");
                else
                    target.Should().BeNull($"{transition} from {status} is not a lifecycle step");
            }
        }
    }

    [Fact]
    public void LifecycleTargets_CheckedInAndActiveHoldSupply_NoShowDoesNot()
    {
        RosterState.IsSupply(RosterAssignmentStatus.CheckedIn).Should().BeTrue();
        RosterState.IsSupply(RosterAssignmentStatus.Active).Should().BeTrue();
        RosterState.IsSupply(RosterAssignmentStatus.NoShow).Should().BeFalse();
    }
}
