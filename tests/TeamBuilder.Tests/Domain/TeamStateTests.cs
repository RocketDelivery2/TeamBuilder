using FluentAssertions;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Domain;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Tests.Domain;

public class TeamStateTests
{
    [Theory]
    // Active + accepting + open slots -> Recruiting
    [InlineData(TeamLifecycleStatus.Active, true, 2, 5, TeamStatus.Recruiting)]
    // Active + accepting + full -> Full
    [InlineData(TeamLifecycleStatus.Active, true, 5, 5, TeamStatus.Full)]
    // Active + closed + open slots -> Active
    [InlineData(TeamLifecycleStatus.Active, false, 2, 5, TeamStatus.Active)]
    // Active + closed + full -> Full
    [InlineData(TeamLifecycleStatus.Active, false, 5, 5, TeamStatus.Full)]
    // Inactive / Disbanded win regardless of recruitment and capacity
    [InlineData(TeamLifecycleStatus.Inactive, false, 0, 5, TeamStatus.Inactive)]
    [InlineData(TeamLifecycleStatus.Inactive, false, 5, 5, TeamStatus.Inactive)]
    [InlineData(TeamLifecycleStatus.Inactive, true, 1, 5, TeamStatus.Inactive)]
    [InlineData(TeamLifecycleStatus.Disbanded, false, 0, 5, TeamStatus.Disbanded)]
    [InlineData(TeamLifecycleStatus.Disbanded, false, 5, 5, TeamStatus.Disbanded)]
    [InlineData(TeamLifecycleStatus.Disbanded, true, 1, 5, TeamStatus.Disbanded)]
    public void LegacyStatus_IsComputedFromLifecycleRecruitmentAndCapacity(
        TeamLifecycleStatus lifecycle, bool accepting, int count, int max, TeamStatus expected)
    {
        TeamState.ToLegacyStatus(lifecycle, accepting, count, max).Should().Be(expected);

        var team = new Team { LifecycleStatus = lifecycle, IsAcceptingMembers = accepting, CurrentMemberCount = count, MaxMembers = max };
        team.LegacyStatus.Should().Be(expected);

        var dto = new TeamDto { LifecycleStatus = lifecycle, IsAcceptingMembers = accepting, CurrentMemberCount = count, MaxMembers = max };
        dto.Status.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 5, 5)]
    [InlineData(3, 5, 2)]
    [InlineData(5, 5, 0)]
    [InlineData(7, 5, 0)] // never negative, even for an (invalid) over-capacity count
    public void OpenSlots_IsNeverNegative(int count, int max, int expected)
    {
        TeamState.OpenSlots(count, max).Should().Be(expected);
        new Team { CurrentMemberCount = count, MaxMembers = max }.OpenSlots.Should().Be(expected);
        new TeamDto { CurrentMemberCount = count, MaxMembers = max }.OpenSlots.Should().Be(expected);
    }

    [Theory]
    [InlineData(4, 5, false)]
    [InlineData(5, 5, true)]
    [InlineData(6, 5, true)]
    public void IsFull_IsCountAtLeastMax(int count, int max, bool expected)
    {
        TeamState.IsFull(count, max).Should().Be(expected);
        new TeamDto { CurrentMemberCount = count, MaxMembers = max }.IsFull.Should().Be(expected);
    }

    [Theory]
    [InlineData(TeamLifecycleStatus.Active, true, 4, 5, true)]
    [InlineData(TeamLifecycleStatus.Active, true, 5, 5, false)]
    [InlineData(TeamLifecycleStatus.Active, false, 4, 5, false)]
    [InlineData(TeamLifecycleStatus.Inactive, true, 0, 5, false)]
    [InlineData(TeamLifecycleStatus.Inactive, false, 0, 5, false)]
    [InlineData(TeamLifecycleStatus.Disbanded, true, 0, 5, false)]
    public void HasVacancies_RequiresActiveAcceptingAndAnOpenSlot(
        TeamLifecycleStatus lifecycle, bool accepting, int count, int max, bool expected)
    {
        TeamState.HasVacancies(lifecycle, accepting, count, max).Should().Be(expected);
        new Team { LifecycleStatus = lifecycle, IsAcceptingMembers = accepting, CurrentMemberCount = count, MaxMembers = max }
            .HasVacancies.Should().Be(expected);
        new TeamDto { LifecycleStatus = lifecycle, IsAcceptingMembers = accepting, CurrentMemberCount = count, MaxMembers = max }
            .HasVacancies.Should().Be(expected);
    }

    [Fact]
    public void NewTeam_DefaultsToActiveAndAccepting()
    {
        var team = new Team { MaxMembers = 3 };

        team.LifecycleStatus.Should().Be(TeamLifecycleStatus.Active);
        team.IsAcceptingMembers.Should().BeTrue();
        team.LegacyStatus.Should().Be(TeamStatus.Recruiting);
    }
}
