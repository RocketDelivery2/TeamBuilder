using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace TeamBuilder.Infrastructure.Persistence;

/// <summary>
/// Recognizes the SQL Server REFERENCE-constraint errors raised when a Player DELETE is
/// rejected because a Team ownership or TeamMember row referencing that player appeared
/// after PlayerService.DeleteAsync checked for them. Deliberately narrow: only error 547 on a
/// DELETE statement naming one of the two expected foreign keys is recognized; every other
/// DbUpdateException (CHECK violations, INSERT/UPDATE FK failures, other tables, transient
/// errors) is left unhandled.
/// </summary>
public static class PlayerDeletionConflictClassifier
{
    private const int ConstraintViolation = 547;
    private const string DeleteReferenceConflict = "The DELETE statement conflicted with the REFERENCE constraint";
    private const string TeamOwnerForeignKeyName = "FK_Teams_Players_OwnerId";
    private const string TeamMemberPlayerForeignKeyName = "FK_TeamMembers_Players_PlayerId";

    public static bool IsOwnedTeamReference(DbUpdateException exception) =>
        IsDeleteReferenceConflict(exception, TeamOwnerForeignKeyName);

    public static bool IsTeamMembershipReference(DbUpdateException exception) =>
        IsDeleteReferenceConflict(exception, TeamMemberPlayerForeignKeyName);

    private static bool IsDeleteReferenceConflict(DbUpdateException exception, string foreignKeyName)
    {
        return exception.InnerException is SqlException { Number: ConstraintViolation } sqlException &&
               sqlException.Message.Contains(DeleteReferenceConflict, StringComparison.OrdinalIgnoreCase) &&
               sqlException.Message.Contains($"\"{foreignKeyName}\"", StringComparison.OrdinalIgnoreCase);
    }
}
