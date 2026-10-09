using FluentAssertions;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Domain.Outbox;

namespace TeamBuilder.Tests.Domain;

public class RosterVacancyTests
{
    [Theory]
    // requiredCount, otherSupply -> previousOpen, open, opened
    [InlineData(10, 9, 0, 1, true)]   // full game, one leaves: 10/10 -> 9/10
    [InlineData(10, 4, 5, 6, true)]   // half full
    [InlineData(10, 0, 9, 10, true)]  // the only player leaves
    [InlineData(1, 0, 0, 1, true)]    // a single tank slot
    [InlineData(10, 10, 0, 0, false)] // overfilled 11/10 -> 10/10: nothing opens
    [InlineData(10, 12, 0, 0, false)] // overfilled 13/10 -> 12/10
    public void Evaluate_OpensOnlyWhenOpenQuantityIncreases(int requiredCount, int otherSupply, int previousOpen, int open, bool opened)
    {
        var change = RosterVacancy.Evaluate(requiredCount, otherSupply);

        (change.PreviousOpenQuantity, change.OpenQuantity, change.Opened).Should().Be((previousOpen, open, opened));
    }

    [Fact]
    public void Evaluate_RefusesNegativeSupply()
    {
        var act = () => RosterVacancy.Evaluate(10, -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(RosterExitReason.PlayerLeft, VacancyReason.PlayerLeft)]
    [InlineData(RosterExitReason.HostRemoved, VacancyReason.HostRemoved)]
    [InlineData(RosterExitReason.NoShow, VacancyReason.NoShow)]
    [InlineData(RosterExitReason.Replaced, null)]
    [InlineData(RosterExitReason.Other, null)]
    [InlineData(null, null)]
    public void ReasonFor_PublishesOnlyTheCurrentVacancyPaths(RosterExitReason? exit, VacancyReason? expected)
    {
        RosterVacancy.ReasonFor(exit).Should().Be(expected);
    }
}
