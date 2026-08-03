using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeamBuilder.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceUniqueTeamMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preflight: the new unique index only allows one active (IsActive = 1)
            // TeamMember row per (TeamId, PlayerId) pair. Fail loudly instead of
            // letting CreateIndex fail with a raw SQL error, or worse, silently
            // succeeding after the data was quietly altered.
            migrationBuilder.Sql(@"
IF EXISTS (
    SELECT 1
    FROM [TeamMembers]
    WHERE [IsActive] = 1
    GROUP BY [TeamId], [PlayerId]
    HAVING COUNT(*) > 1
)
BEGIN
    RAISERROR('Cannot apply migration EnforceUniqueTeamMembership: duplicate active TeamMember rows exist for one or more (TeamId, PlayerId) pairs. Resolve the duplicate active memberships before re-running this migration.', 16, 1);
    RETURN;
END
");

            migrationBuilder.DropIndex(
                name: "IX_TeamMembers_TeamId_PlayerId",
                table: "TeamMembers");

            migrationBuilder.CreateIndex(
                name: "UX_TeamMembers_TeamId_PlayerId",
                table: "TeamMembers",
                columns: new[] { "TeamId", "PlayerId" },
                unique: true,
                filter: "[IsActive] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_TeamMembers_TeamId_PlayerId",
                table: "TeamMembers");

            migrationBuilder.CreateIndex(
                name: "IX_TeamMembers_TeamId_PlayerId",
                table: "TeamMembers",
                columns: new[] { "TeamId", "PlayerId" });
        }
    }
}
