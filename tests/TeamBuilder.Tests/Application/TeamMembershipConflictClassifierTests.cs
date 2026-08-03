using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Infrastructure.Persistence;

namespace TeamBuilder.Tests.Application;

public class TeamMembershipConflictClassifierTests
{
    [Theory]
    [InlineData(2627)]
    [InlineData(2601)]
    public void IsDuplicateTeamMembership_ShouldReturnTrue_ForRecognizedTeamMemberUniqueIndexViolation(int errorNumber)
    {
        // Arrange
        var sqlException = SqlExceptionTestFactory.Create(
            errorNumber,
            "Violation of UNIQUE KEY constraint 'UX_TeamMembers_TeamId_PlayerId'. Cannot insert duplicate key in object 'dbo.TeamMembers'.");
        var dbUpdateException = new DbUpdateException("Update failed.", sqlException);

        // Act
        var result = TeamMembershipConflictClassifier.IsDuplicateTeamMembership(dbUpdateException);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsDuplicateTeamMembership_ShouldReturnFalse_ForUnrelatedUniqueConstraintViolation()
    {
        // Arrange: same SQL error number, but a different constraint (e.g. Players.Username).
        var sqlException = SqlExceptionTestFactory.Create(
            2601,
            "Cannot insert duplicate key row in object 'dbo.Players' with unique index 'IX_Players_Username'.");
        var dbUpdateException = new DbUpdateException("Update failed.", sqlException);

        // Act
        var result = TeamMembershipConflictClassifier.IsDuplicateTeamMembership(dbUpdateException);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsDuplicateTeamMembership_ShouldReturnFalse_ForUnrelatedDbUpdateException()
    {
        // Arrange: a DbUpdateException not caused by a SqlException at all (e.g. a mapping
        // or FK error surfaced without an inner SqlException in this simulated case).
        var dbUpdateException = new DbUpdateException("Some other persistence failure.", new InvalidOperationException());

        // Act
        var result = TeamMembershipConflictClassifier.IsDuplicateTeamMembership(dbUpdateException);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsDuplicateTeamMembership_ShouldReturnFalse_ForNonDuplicateSqlError()
    {
        // Arrange: a SqlException with an unrelated error number (e.g. timeout).
        var sqlException = SqlExceptionTestFactory.Create(-2, "Timeout expired.");
        var dbUpdateException = new DbUpdateException("Update failed.", sqlException);

        // Act
        var result = TeamMembershipConflictClassifier.IsDuplicateTeamMembership(dbUpdateException);

        // Assert
        result.Should().BeFalse();
    }
}
