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
    public const string AddPlayerIdentities = "20261003013542_AddPlayerIdentities";
    public const string EnforceCapacityDatabaseGuards = "20261004051605_EnforceCapacityDatabaseGuards";
    public const string SeparateTeamLifecycleFromRecruitment = "20261004061312_SeparateTeamLifecycleFromRecruitment";
    public const string AddEventOccurrenceSchedulingFoundation = "20261004172211_AddEventOccurrenceSchedulingFoundation";
}
