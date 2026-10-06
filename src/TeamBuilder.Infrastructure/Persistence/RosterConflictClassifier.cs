using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Infrastructure.Data.Configurations;

namespace TeamBuilder.Infrastructure.Persistence;

/// <summary>
/// Recognizes the SQL Server duplicate-key errors raised by the roster unique indexes when a
/// concurrent request committed the same row first. Deliberately narrow: only 2601/2627 naming
/// the expected index is recognized; every other DbUpdateException is left unhandled.
/// </summary>
public static class RosterConflictClassifier
{
    private const int UniqueConstraintViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    public static bool IsDuplicateSupplyAssignment(DbUpdateException exception) =>
        IsUniqueViolation(exception, RosterAssignmentConfiguration.SupplyUniqueIndexName);

    public static bool IsDuplicateRequirementRole(DbUpdateException exception) =>
        IsUniqueViolation(exception, RosterRequirementConfiguration.OccurrenceRoleUniqueIndexName);

    private static bool IsUniqueViolation(DbUpdateException exception, string indexName)
    {
        return exception.InnerException is SqlException { } sqlException &&
               (sqlException.Number == UniqueConstraintViolation || sqlException.Number == UniqueIndexViolation) &&
               sqlException.Message.Contains(indexName, StringComparison.OrdinalIgnoreCase);
    }
}
