using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Infrastructure.Data.Configurations;

namespace TeamBuilder.Infrastructure.Persistence;

/// <summary>
/// Recognizes the SQL Server duplicate-key errors raised by the roster unique indexes when a
/// concurrent request committed the same row first, and the 547 DELETE conflict raised when an
/// occurrence with participation history is deleted. Deliberately narrow: only 2601/2627 naming
/// the expected index, or 547 on a DELETE naming the expected FK, is recognized; every other
/// DbUpdateException is left unhandled.
/// </summary>
public static class RosterConflictClassifier
{
    private const int UniqueConstraintViolation = 2627;
    private const int UniqueIndexViolation = 2601;
    private const int ConstraintViolation = 547;
    private const string DeleteReferenceConflict = "The DELETE statement conflicted with the REFERENCE constraint";

    public static bool IsDuplicateSupplyAssignment(DbUpdateException exception) =>
        IsUniqueViolation(exception, RosterAssignmentConfiguration.SupplyUniqueIndexName);

    public static bool IsDuplicateRequirementRole(DbUpdateException exception) =>
        IsUniqueViolation(exception, RosterRequirementConfiguration.OccurrenceRoleUniqueIndexName);

    /// <summary>A concurrent "notify me" for the same (player, occurrence, requirement) committed first.</summary>
    public static bool IsDuplicateRosterSubscription(DbUpdateException exception) =>
        IsUniqueViolation(exception, OccurrenceRosterSubscriptionConfiguration.PlayerOccurrenceRequirementUniqueIndexName);

    /// <summary>Another worker already notified this player about this outbox event.</summary>
    public static bool IsDuplicateSourceEventNotification(DbUpdateException exception) =>
        IsUniqueViolation(exception, InAppNotificationConfiguration.SourceEventPlayerUniqueIndexName);

    /// <summary>The same fact was already staged in the outbox (same deduplication key).</summary>
    public static bool IsDuplicateOutboxMessage(DbUpdateException exception) =>
        IsUniqueViolation(exception, OutboxMessageConfiguration.DeduplicationKeyUniqueIndexName);

    /// <summary>
    /// An occurrence DELETE refused because an assignment appeared after EventService.DeleteAsync
    /// checked for participation history: either by the NO ACTION FK from RosterAssignments to
    /// Events, or, for an assignment linked to a requirement, by the NO ACTION requirement FK
    /// when the cascade reaches that requirement first.
    /// </summary>
    public static bool IsOccurrenceWithParticipationHistoryDelete(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: ConstraintViolation } sqlException &&
        sqlException.Message.Contains(DeleteReferenceConflict, StringComparison.OrdinalIgnoreCase) &&
        (sqlException.Message.Contains($"\"{RosterAssignmentConfiguration.OccurrenceForeignKeyName}\"", StringComparison.OrdinalIgnoreCase) ||
         sqlException.Message.Contains($"\"{RosterAssignmentConfiguration.RequirementForeignKeyName}\"", StringComparison.OrdinalIgnoreCase));

    private static bool IsUniqueViolation(DbUpdateException exception, string indexName)
    {
        return exception.InnerException is SqlException { } sqlException &&
               (sqlException.Number == UniqueConstraintViolation || sqlException.Number == UniqueIndexViolation) &&
               sqlException.Message.Contains(indexName, StringComparison.OrdinalIgnoreCase);
    }
}
