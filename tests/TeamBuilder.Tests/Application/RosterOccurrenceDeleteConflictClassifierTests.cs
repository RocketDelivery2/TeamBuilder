using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Infrastructure.Persistence;

namespace TeamBuilder.Tests.Application;

public class RosterOccurrenceDeleteConflictClassifierTests
{
    private static DbUpdateException Wrap(int number, string message) =>
        new("Update failed.", SqlExceptionTestFactory.Create(number, message));

    [Theory]
    [InlineData("FK_RosterAssignments_Events_OccurrenceId")]
    [InlineData("FK_RosterAssignments_RosterRequirements_RequirementId_OccurrenceId")]
    public void RecognizesAnOccurrenceDeleteRefusedByParticipationHistory(string foreignKey)
    {
        var ex = Wrap(547, $"The DELETE statement conflicted with the REFERENCE constraint \"{foreignKey}\". The conflict occurred in database \"TeamBuilder\", table \"dbo.RosterAssignments\".");

        RosterConflictClassifier.IsOccurrenceWithParticipationHistoryDelete(ex).Should().BeTrue();
    }

    [Theory]
    // An INSERT against a missing occurrence is not a delete race.
    [InlineData(547, "The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_RosterAssignments_Events_OccurrenceId\".")]
    // A different REFERENCE constraint.
    [InlineData(547, "The DELETE statement conflicted with the REFERENCE constraint \"FK_RosterAssignments_Players_PlayerId\".")]
    // Right constraint, wrong error number.
    [InlineData(2601, "The DELETE statement conflicted with the REFERENCE constraint \"FK_RosterAssignments_Events_OccurrenceId\".")]
    public void IgnoresUnrelatedSqlErrors(int number, string message)
    {
        RosterConflictClassifier.IsOccurrenceWithParticipationHistoryDelete(Wrap(number, message)).Should().BeFalse();
    }

    [Fact]
    public void IgnoresDbUpdateExceptionWithoutSqlException()
    {
        var ex = new DbUpdateException("Some other persistence failure.", new InvalidOperationException());

        RosterConflictClassifier.IsOccurrenceWithParticipationHistoryDelete(ex).Should().BeFalse();
    }
}
