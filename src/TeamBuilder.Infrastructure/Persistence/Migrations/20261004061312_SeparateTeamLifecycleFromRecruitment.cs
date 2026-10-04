using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeamBuilder.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Splits the persisted legacy Teams.Status (which conflated lifecycle with recruitment and
    /// capacity) into LifecycleStatus + IsAcceptingMembers. Deliberately additive/backfill/drop
    /// rather than a rename-in-place, so the value mapping is explicit and auditable:
    ///
    ///   legacy Status (int)  -> LifecycleStatus (int)  IsAcceptingMembers
    ///   Active      (1)      -> Active    (1)          false  (recruitment paused)
    ///   Recruiting  (2)      -> Active    (1)          true
    ///   Full        (3)      -> Active    (1)          true   (Full was automatic capacity
    ///                                                          state of a recruiting team)
    ///   Inactive    (4)      -> Inactive  (2)          false
    ///   Disbanded   (5)      -> Disbanded (3)          false
    ///
    /// Capacity (Full) is no longer stored; it is derived from CurrentMemberCount/MaxMembers.
    /// CK_Teams_CurrentMemberCount_Bounds is untouched.
    ///
    /// Down is a SEMANTIC rollback: it recomputes the legacy Status from the new columns and
    /// the current capacity (Inactive -> Inactive, Disbanded -> Disbanded, Active and
    /// count >= max -> Full, Active and accepting -> Recruiting, otherwise Active). It does not
    /// reproduce historically stale legacy values byte for byte (for example a stored Full on a
    /// team that was no longer at capacity comes back as Recruiting).
    /// </summary>
    public partial class SeparateTeamLifecycleFromRecruitment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preflight: refuse to guess a mapping for a legacy value outside the enum.
            migrationBuilder.Sql(@"
DECLARE @unknownTeams int;
DECLARE @exampleTeamId nvarchar(36);

SELECT @unknownTeams = COUNT(*), @exampleTeamId = MIN(CONVERT(nvarchar(36), [Id]))
FROM [Teams]
WHERE [Status] NOT IN (1, 2, 3, 4, 5);

IF @unknownTeams > 0
BEGIN
    RAISERROR('Cannot apply migration SeparateTeamLifecycleFromRecruitment: %d team(s) have a Status outside the known TeamStatus values 1-5 (for example team %s). Correct those rows before re-running this migration.', 16, 1, @unknownTeams, @exampleTeamId);
    RETURN;
END
");

            // 1. + 2. Add the new columns. The defaults only exist to populate existing rows
            // before the explicit backfill below; the application always writes both values.
            migrationBuilder.AddColumn<int>(
                name: "LifecycleStatus",
                table: "Teams",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "IsAcceptingMembers",
                table: "Teams",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // 3. Backfill from the OLD persisted Status (mapping table above).
            migrationBuilder.Sql(@"
UPDATE [Teams]
SET [LifecycleStatus] = CASE [Status]
        WHEN 1 THEN 1 -- Active     -> Active
        WHEN 2 THEN 1 -- Recruiting -> Active
        WHEN 3 THEN 1 -- Full       -> Active
        WHEN 4 THEN 2 -- Inactive   -> Inactive
        WHEN 5 THEN 3 -- Disbanded  -> Disbanded
    END,
    [IsAcceptingMembers] = CASE [Status]
        WHEN 2 THEN CAST(1 AS bit) -- Recruiting
        WHEN 3 THEN CAST(1 AS bit) -- Full
        ELSE CAST(0 AS bit)        -- Active (paused), Inactive, Disbanded
    END;
");

            // 4. + 5. Drop the legacy index and column.
            migrationBuilder.DropIndex(
                name: "IX_Teams_Status",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Teams");

            // 6. Discovery index (lifecycleStatus / hasVacancies / legacy status filters).
            migrationBuilder.CreateIndex(
                name: "IX_Teams_LifecycleStatus_IsAcceptingMembers",
                table: "Teams",
                columns: new[] { "LifecycleStatus", "IsAcceptingMembers" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DECLARE @unknownTeams int;
DECLARE @exampleTeamId nvarchar(36);

SELECT @unknownTeams = COUNT(*), @exampleTeamId = MIN(CONVERT(nvarchar(36), [Id]))
FROM [Teams]
WHERE [LifecycleStatus] NOT IN (1, 2, 3);

IF @unknownTeams > 0
BEGIN
    RAISERROR('Cannot revert migration SeparateTeamLifecycleFromRecruitment: %d team(s) have a LifecycleStatus outside the known values 1-3 (for example team %s). Correct those rows before reverting.', 16, 1, @unknownTeams, @exampleTeamId);
    RETURN;
END
");

            migrationBuilder.DropIndex(
                name: "IX_Teams_LifecycleStatus_IsAcceptingMembers",
                table: "Teams");

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "Teams",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Semantic reconstruction of the legacy Status (see the class summary).
            migrationBuilder.Sql(@"
UPDATE [Teams]
SET [Status] = CASE
        WHEN [LifecycleStatus] = 2 THEN 4                    -- Inactive
        WHEN [LifecycleStatus] = 3 THEN 5                    -- Disbanded
        WHEN [CurrentMemberCount] >= [MaxMembers] THEN 3     -- Active + full     -> Full
        WHEN [IsAcceptingMembers] = 1 THEN 2                 -- Active + accepting -> Recruiting
        ELSE 1                                               -- Active + closed    -> Active
    END;
");

            migrationBuilder.DropColumn(
                name: "IsAcceptingMembers",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "LifecycleStatus",
                table: "Teams");

            migrationBuilder.CreateIndex(
                name: "IX_Teams_Status",
                table: "Teams",
                column: "Status");
        }
    }
}
