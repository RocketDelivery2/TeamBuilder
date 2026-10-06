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

        var snapshot = RosterState.Compute(
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

        snapshot.SupplyCount.Should().Be(4);
        snapshot.UnlinkedSupplyCount.Should().Be(1);
        snapshot.Requirements[guards].Should().Be(new RequirementSupply(guards, 2, 3, 0));
        snapshot.Requirements[centers].Should().Be(new RequirementSupply(centers, 1, 0, 1));
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
}
