using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeamBuilder.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceCapacityDatabaseGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preflight: CK_Teams_CurrentMemberCount_Bounds needs every stored count to lie in
            // [0, MaxMembers]. Active TeamMember rows are the occupancy authority, so if any team
            // actually holds more active members than MaxMembers, fail loudly rather than
            // silently raising MaxMembers or deactivating members.
            migrationBuilder.Sql(@"
DECLARE @overfilledTeams int;
DECLARE @exampleTeamId nvarchar(36);

SELECT @overfilledTeams = COUNT(*), @exampleTeamId = MIN(CONVERT(nvarchar(36), t.[Id]))
FROM [Teams] t
CROSS APPLY (
    SELECT COUNT(*) AS [ActiveCount]
    FROM [TeamMembers] tm
    WHERE tm.[TeamId] = t.[Id] AND tm.[IsActive] = 1
) a
WHERE a.[ActiveCount] > t.[MaxMembers];

IF @overfilledTeams > 0
BEGIN
    RAISERROR('Cannot apply migration EnforceCapacityDatabaseGuards: %d team(s) have more active TeamMember rows than MaxMembers (for example team %s). Raise MaxMembers or deactivate surplus memberships for those teams before re-running this migration.', 16, 1, @overfilledTeams, @exampleTeamId);
    RETURN;
END
");

            // Reconcile every stored count to the actual active membership count, so the
            // CHECK below is added against consistent data. A no-op on a consistent database.
            migrationBuilder.Sql(@"
UPDATE t
SET t.[CurrentMemberCount] = a.[ActiveCount]
FROM [Teams] t
CROSS APPLY (
    SELECT COUNT(*) AS [ActiveCount]
    FROM [TeamMembers] tm
    WHERE tm.[TeamId] = t.[Id] AND tm.[IsActive] = 1
) a
WHERE t.[CurrentMemberCount] <> a.[ActiveCount];
");

            migrationBuilder.DropForeignKey(
                name: "FK_TeamMembers_Players_PlayerId",
                table: "TeamMembers");

            migrationBuilder.DropForeignKey(
                name: "FK_Teams_Players_OwnerId",
                table: "Teams");

            migrationBuilder.DropIndex(
                name: "IX_TeamMembers_IsActive",
                table: "TeamMembers");

            migrationBuilder.DropIndex(
                name: "IX_TeamMembers_PlayerId",
                table: "TeamMembers");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Teams_CurrentMemberCount_Bounds",
                table: "Teams",
                sql: "[CurrentMemberCount] >= 0 AND [CurrentMemberCount] <= [MaxMembers]");

            migrationBuilder.CreateIndex(
                name: "IX_TeamMembers_PlayerId_IsActive",
                table: "TeamMembers",
                columns: new[] { "PlayerId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_TeamMembers_TeamId_IsActive",
                table: "TeamMembers",
                columns: new[] { "TeamId", "IsActive" });

            migrationBuilder.AddForeignKey(
                name: "FK_TeamMembers_Players_PlayerId",
                table: "TeamMembers",
                column: "PlayerId",
                principalTable: "Players",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Teams_Players_OwnerId",
                table: "Teams",
                column: "OwnerId",
                principalTable: "Players",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TeamMembers_Players_PlayerId",
                table: "TeamMembers");

            migrationBuilder.DropForeignKey(
                name: "FK_Teams_Players_OwnerId",
                table: "Teams");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Teams_CurrentMemberCount_Bounds",
                table: "Teams");

            migrationBuilder.DropIndex(
                name: "IX_TeamMembers_PlayerId_IsActive",
                table: "TeamMembers");

            migrationBuilder.DropIndex(
                name: "IX_TeamMembers_TeamId_IsActive",
                table: "TeamMembers");

            migrationBuilder.CreateIndex(
                name: "IX_TeamMembers_IsActive",
                table: "TeamMembers",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_TeamMembers_PlayerId",
                table: "TeamMembers",
                column: "PlayerId");

            migrationBuilder.AddForeignKey(
                name: "FK_TeamMembers_Players_PlayerId",
                table: "TeamMembers",
                column: "PlayerId",
                principalTable: "Players",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Teams_Players_OwnerId",
                table: "Teams",
                column: "OwnerId",
                principalTable: "Players",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
