using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Infrastructure.Data.Configurations;

namespace TeamBuilder.Infrastructure.Persistence;

/// <summary>
/// Recognizes the SQL Server duplicate-key error raised by the
/// UX_Events_SeriesId_ScheduledStartUtc unique filtered index, i.e. a concurrent
/// materialization already inserted an occurrence of the same series at the same instant.
/// Deliberately narrow: any other DbUpdateException is left unhandled.
/// </summary>
public static class EventSeriesOccurrenceConflictClassifier
{
    private const int UniqueConstraintViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    public static bool IsDuplicateSeriesOccurrence(DbUpdateException exception)
    {
        return exception.InnerException is SqlException { } sqlException &&
               (sqlException.Number == UniqueConstraintViolation || sqlException.Number == UniqueIndexViolation) &&
               sqlException.Message.Contains(EventOccurrenceConfiguration.SeriesStartUniqueIndexName, StringComparison.OrdinalIgnoreCase);
    }
}
