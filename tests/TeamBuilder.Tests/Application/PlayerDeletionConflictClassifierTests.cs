using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Infrastructure.Persistence;

namespace TeamBuilder.Tests.Application;

public class PlayerDeletionConflictClassifierTests
{
    private const string OwnerReferenceMessage =
        "The DELETE statement conflicted with the REFERENCE constraint \"FK_Teams_Players_OwnerId\". The conflict occurred in database \"TeamBuilder\", table \"dbo.Teams\", column 'OwnerId'.";

    private const string MembershipReferenceMessage =
        "The DELETE statement conflicted with the REFERENCE constraint \"FK_TeamMembers_Players_PlayerId\". The conflict occurred in database \"TeamBuilder\", table \"dbo.TeamMembers\", column 'PlayerId'.";

    private static DbUpdateException Wrap(int number, string message) =>
        new("Update failed.", SqlExceptionTestFactory.Create(number, message));

    [Fact]
    public void RecognizesOwnerReferenceConflict_Only()
    {
        var ex = Wrap(547, OwnerReferenceMessage);

        PlayerDeletionConflictClassifier.IsOwnedTeamReference(ex).Should().BeTrue();
        PlayerDeletionConflictClassifier.IsTeamMembershipReference(ex).Should().BeFalse();
    }

    [Fact]
    public void RecognizesMembershipReferenceConflict_Only()
    {
        var ex = Wrap(547, MembershipReferenceMessage);

        PlayerDeletionConflictClassifier.IsTeamMembershipReference(ex).Should().BeTrue();
        PlayerDeletionConflictClassifier.IsOwnedTeamReference(ex).Should().BeFalse();
    }

    [Theory]
    // Same error number, but a CHECK constraint violation.
    [InlineData(547, "The UPDATE statement conflicted with the CHECK constraint \"CK_Teams_CurrentMemberCount_Bounds\". The conflict occurred in database \"TeamBuilder\", table \"dbo.Teams\".")]
    // Same FK, but an INSERT referencing a missing player (not a delete race).
    [InlineData(547, "The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_TeamMembers_Players_PlayerId\". The conflict occurred in database \"TeamBuilder\", table \"dbo.Players\", column 'Id'.")]
    // A different REFERENCE constraint on a player delete.
    [InlineData(547, "The DELETE statement conflicted with the REFERENCE constraint \"FK_PlayerIdentities_Players_PlayerId\". The conflict occurred in database \"TeamBuilder\", table \"dbo.PlayerIdentities\", column 'PlayerId'.")]
    // Right constraint name, wrong error number.
    [InlineData(2601, "The DELETE statement conflicted with the REFERENCE constraint \"FK_Teams_Players_OwnerId\".")]
    public void IgnoresUnrelatedSqlErrors(int number, string message)
    {
        var ex = Wrap(number, message);

        PlayerDeletionConflictClassifier.IsOwnedTeamReference(ex).Should().BeFalse();
        PlayerDeletionConflictClassifier.IsTeamMembershipReference(ex).Should().BeFalse();
    }

    [Fact]
    public void IgnoresDbUpdateExceptionWithoutSqlException()
    {
        var ex = new DbUpdateException("Some other persistence failure.", new InvalidOperationException());

        PlayerDeletionConflictClassifier.IsOwnedTeamReference(ex).Should().BeFalse();
        PlayerDeletionConflictClassifier.IsTeamMembershipReference(ex).Should().BeFalse();
    }
}
