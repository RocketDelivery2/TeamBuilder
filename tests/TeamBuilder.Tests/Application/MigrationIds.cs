namespace TeamBuilder.Tests.Application;

/// <summary>
/// The exact migration ids (in applied order) used by the real-SQL-Server integration tests
/// to migrate to a specific point in history. Kept in one place so a future migration rename
/// only needs one edit.
/// </summary>
internal static class MigrationIds
{
    public const string InitialCreate = "20260511064428_InitialCreate";
    public const string EnforceUniqueTeamMembership = "20260803064415_EnforceUniqueTeamMembership";
    public const string EnforceUniquePendingJoinRequest = "20260803100000_EnforceUniquePendingJoinRequest";
}
