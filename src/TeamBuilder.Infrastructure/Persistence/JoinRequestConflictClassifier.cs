using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace TeamBuilder.Infrastructure.Persistence;

/// <summary>
/// Recognizes the specific SQL Server duplicate-key error raised by the
/// UX_JoinRequests_TeamId_PlayerId_Pending unique filtered index so it can be
/// mapped to a friendly, deterministic conflict instead of a generic 500 error.
/// Deliberately narrow: any other DbUpdateException (unrelated constraint,
/// unrelated table, transient failure, etc.) is left unhandled so it is not
/// mislabeled as a duplicate-pending-request conflict.
/// </summary>
public static class JoinRequestConflictClassifier
{
    private const int UniqueConstraintViolation = 2627;
    private const int UniqueIndexViolation = 2601;
    private const string PendingJoinRequestUniqueIndexName = "UX_JoinRequests_TeamId_PlayerId_Pending";

    public static bool IsDuplicatePendingJoinRequest(DbUpdateException exception)
    {
        return exception.InnerException is SqlException { } sqlException &&
               (sqlException.Number == UniqueConstraintViolation || sqlException.Number == UniqueIndexViolation) &&
               sqlException.Message.Contains(PendingJoinRequestUniqueIndexName, StringComparison.OrdinalIgnoreCase);
    }
}
