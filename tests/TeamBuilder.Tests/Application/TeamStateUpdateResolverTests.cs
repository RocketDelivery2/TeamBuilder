using FluentAssertions;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Validation;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Tests.Application;

public class TeamStateUpdateResolverTests
{
    [Theory]
    [InlineData(TeamStatus.Recruiting, TeamLifecycleStatus.Active, true)]
    [InlineData(TeamStatus.Active, TeamLifecycleStatus.Active, false)]
    [InlineData(TeamStatus.Inactive, TeamLifecycleStatus.Inactive, false)]
    [InlineData(TeamStatus.Disbanded, TeamLifecycleStatus.Disbanded, false)]
    public void LegacyStatus_MapsPerDirectorTable(TeamStatus legacy, TeamLifecycleStatus lifecycle, bool accepting)
    {
        foreach (var (currentLifecycle, currentAccepting) in new[]
                 {
                     (TeamLifecycleStatus.Active, true), (TeamLifecycleStatus.Active, false),
                     (TeamLifecycleStatus.Inactive, false), (TeamLifecycleStatus.Disbanded, false)
                 })
        {
            TeamStateUpdateResolver.Resolve(currentLifecycle, currentAccepting, new UpdateTeamDto { Status = legacy })
                .Should().Be((lifecycle, accepting));
        }
    }

    [Fact]
    public void LegacyFull_IsRejected()
    {
        var act = () => TeamStateUpdateResolver.Resolve(TeamLifecycleStatus.Active, true, new UpdateTeamDto { Status = TeamStatus.Full });

        act.Should().Throw<ArgumentException>().WithMessage(TeamStateUpdateResolver.FullCannotBeSetMessage + "*");
    }

    [Theory]
    [InlineData(TeamStatus.Recruiting, TeamLifecycleStatus.Active, null)]
    [InlineData(TeamStatus.Inactive, null, false)]
    [InlineData(TeamStatus.Full, TeamLifecycleStatus.Active, true)]
    public void LegacyStatus_WithNewFields_IsRejectedAsAmbiguous(TeamStatus legacy, TeamLifecycleStatus? lifecycle, bool? accepting)
    {
        var act = () => TeamStateUpdateResolver.Resolve(
            TeamLifecycleStatus.Active, true,
            new UpdateTeamDto { Status = legacy, LifecycleStatus = lifecycle, IsAcceptingMembers = accepting });

        act.Should().Throw<ArgumentException>().WithMessage(TeamStateUpdateResolver.AmbiguousStatusMessage + "*");
    }

    [Theory]
    [InlineData(TeamLifecycleStatus.Inactive)]
    [InlineData(TeamLifecycleStatus.Disbanded)]
    public void MovingToNonActive_ClosesRecruitment_AndRejectsExplicitAccepting(TeamLifecycleStatus target)
    {
        TeamStateUpdateResolver.Resolve(TeamLifecycleStatus.Active, true, new UpdateTeamDto { LifecycleStatus = target })
            .Should().Be((target, false));
        TeamStateUpdateResolver.Resolve(TeamLifecycleStatus.Active, true, new UpdateTeamDto { LifecycleStatus = target, IsAcceptingMembers = false })
            .Should().Be((target, false));

        var act = () => TeamStateUpdateResolver.Resolve(
            TeamLifecycleStatus.Active, true, new UpdateTeamDto { LifecycleStatus = target, IsAcceptingMembers = true });
        act.Should().Throw<ArgumentException>().WithMessage(TeamStateUpdateResolver.NotActiveCannotAcceptMessage + "*");

        var stayAndOpen = () => TeamStateUpdateResolver.Resolve(target, false, new UpdateTeamDto { IsAcceptingMembers = true });
        stayAndOpen.Should().Throw<ArgumentException>().WithMessage(TeamStateUpdateResolver.NotActiveCannotAcceptMessage + "*");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // even if a stale stored flag were true, reactivation never implicitly reopens
    public void Reactivation_WithoutExplicitAccepting_StaysClosed(bool storedAccepting)
    {
        TeamStateUpdateResolver.Resolve(TeamLifecycleStatus.Inactive, storedAccepting, new UpdateTeamDto { LifecycleStatus = TeamLifecycleStatus.Active })
            .Should().Be((TeamLifecycleStatus.Active, false));
    }

    [Fact]
    public void Reactivation_WithExplicitAccepting_Opens()
    {
        TeamStateUpdateResolver.Resolve(
                TeamLifecycleStatus.Disbanded, false,
                new UpdateTeamDto { LifecycleStatus = TeamLifecycleStatus.Active, IsAcceptingMembers = true })
            .Should().Be((TeamLifecycleStatus.Active, true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActiveTeam_OmittedFields_KeepCurrentState(bool accepting)
    {
        TeamStateUpdateResolver.Resolve(TeamLifecycleStatus.Active, accepting, new UpdateTeamDto { Name = "x" })
            .Should().Be((TeamLifecycleStatus.Active, accepting));
    }

    [Fact]
    public void ActiveTeam_ToggleRecruitment()
    {
        TeamStateUpdateResolver.Resolve(TeamLifecycleStatus.Active, true, new UpdateTeamDto { IsAcceptingMembers = false })
            .Should().Be((TeamLifecycleStatus.Active, false));
        TeamStateUpdateResolver.Resolve(TeamLifecycleStatus.Active, false, new UpdateTeamDto { IsAcceptingMembers = true })
            .Should().Be((TeamLifecycleStatus.Active, true));
    }
}
