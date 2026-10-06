using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Infrastructure.Persistence;

namespace TeamBuilder.Tests.Application;

/// <summary>Only a duplicate on UX_Events_SeriesId_ScheduledStartUtc counts as "already materialized".</summary>
public class EventSeriesOccurrenceConflictClassifierTests
{
    [Theory]
    [InlineData(2601)]
    [InlineData(2627)]
    public void DuplicateOnTheSeriesStartIndex_IsRecognized(int number)
    {
        var exception = Wrap(number, "Cannot insert duplicate key row in object 'dbo.Events' with unique index 'UX_Events_SeriesId_ScheduledStartUtc'.");

        EventSeriesOccurrenceConflictClassifier.IsDuplicateSeriesOccurrence(exception).Should().BeTrue();
    }

    [Theory]
    [InlineData(2601, "Cannot insert duplicate key row in object 'dbo.TeamMembers' with unique index 'UX_TeamMembers_TeamId_PlayerId_Active'.")]
    [InlineData(547, "The INSERT statement conflicted with the CHECK constraint \"CK_Events_MaxParticipants\".")]
    [InlineData(1205, "Transaction was deadlocked on lock resources with another process and has been chosen as the deadlock victim.")]
    [InlineData(547, "The INSERT statement conflicted with the FOREIGN KEY constraint. UX_Events_SeriesId_ScheduledStartUtc")]
    public void OtherDatabaseErrors_AreNotRecognized(int number, string message)
    {
        EventSeriesOccurrenceConflictClassifier.IsDuplicateSeriesOccurrence(Wrap(number, message)).Should().BeFalse();
    }

    [Fact]
    public void NonSqlInnerException_IsNotRecognized()
    {
        var exception = new DbUpdateException("x", new InvalidOperationException("UX_Events_SeriesId_ScheduledStartUtc"));

        EventSeriesOccurrenceConflictClassifier.IsDuplicateSeriesOccurrence(exception).Should().BeFalse();
    }

    private static DbUpdateException Wrap(int number, string message) =>
        new("An error occurred while saving the entity changes.", SqlExceptionTestFactory.Create(number, message));
}
