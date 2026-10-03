using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Infrastructure.Data.Configurations;

namespace TeamBuilder.Infrastructure.Persistence;

/// <summary>
/// Recognizes the SQL Server duplicate-key errors that onboarding can hit when it loses a race:
/// the UX_PlayerIdentities_Issuer_Subject index (identity already linked) and the
/// IX_Players_Username index (username taken). Deliberately narrow: any other
/// DbUpdateException is left unhandled so it is not mislabeled as a conflict.
/// </summary>
public static class PlayerOnboardingConflictClassifier
{
    private const int UniqueConstraintViolation = 2627;
    private const int UniqueIndexViolation = 2601;
    private const string PlayerUsernameUniqueIndexName = "IX_Players_Username";

    public static bool IsDuplicateExternalIdentity(DbUpdateException exception)
        => IsDuplicateKeyOn(exception, PlayerIdentityConfiguration.IssuerSubjectIndexName);

    public static bool IsDuplicateUsername(DbUpdateException exception)
        => IsDuplicateKeyOn(exception, PlayerUsernameUniqueIndexName);

    private static bool IsDuplicateKeyOn(DbUpdateException exception, string indexName)
    {
        return exception.InnerException is SqlException { } sqlException &&
               (sqlException.Number == UniqueConstraintViolation || sqlException.Number == UniqueIndexViolation) &&
               sqlException.Message.Contains(indexName, StringComparison.OrdinalIgnoreCase);
    }
}
