using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace TeamBuilder.Infrastructure.Persistence;

/// <summary>
/// Recognizes the specific SQL Server duplicate-key error raised by the
/// UX_TeamMembers_TeamId_PlayerId unique index so it can be mapped to a
/// friendly, deterministic conflict instead of a generic 500 error.
/// Deliberately narrow: any other DbUpdateException (unrelated constraint,
/// unrelated table, transient failure, etc.) is left unhandled so it is not
/// mislabeled as a duplicate-membership conflict.
/// </summary>
public static class TeamMembershipConflictClassifier
{
    private const int UniqueConstraintViolation = 2627;
    private const int UniqueIndexViolation = 2601;
    private const string TeamMembershipUniqueIndexName = "UX_TeamMembers_TeamId_PlayerId";

    public static bool IsDuplicateTeamMembership(DbUpdateException exception)
    {
        return exception.InnerException is SqlException { } sqlException &&
               (sqlException.Number == UniqueConstraintViolation || sqlException.Number == UniqueIndexViolation) &&
               sqlException.Message.Contains(TeamMembershipUniqueIndexName, StringComparison.OrdinalIgnoreCase);
    }
}
