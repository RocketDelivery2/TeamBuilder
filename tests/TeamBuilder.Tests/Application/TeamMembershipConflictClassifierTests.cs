using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Infrastructure.Persistence;

namespace TeamBuilder.Tests.Application;

public class TeamMembershipConflictClassifierTests
{
    [Theory]
    [InlineData(2627, "Violation of UNIQUE KEY constraint 'UX_TeamMembers_TeamId_PlayerId'. Cannot insert duplicate key in object 'dbo.TeamMembers'.")]
    [InlineData(2601, "Cannot insert duplicate key row in object 'dbo.TeamMembers' with unique index 'UX_TeamMembers_TeamId_PlayerId'.")]
    public void IsDuplicateTeamMembership_ShouldReturnTrue_ForRecognizedTeamMemberUniqueIndexViolation(int errorNumber, string message)
    {
        // Arrange: 2627 (constraint violation) and 2601 (index violation) are raised by SQL
        // Server with distinctly different message shapes, so each case uses its own
        // realistic message rather than sharing one.
        var sqlException = SqlExceptionTestFactory.Create(errorNumber, message);
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
