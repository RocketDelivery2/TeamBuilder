using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeamBuilder.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceUniquePendingJoinRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preflight: the new unique index only allows one pending (Status = 1,
            // RequestStatus.Pending) JoinRequest row per (TeamId, PlayerId) pair. Fail
            // loudly instead of letting CreateIndex fail with a raw SQL error, or worse,
            // silently succeeding after the data was quietly altered.
            migrationBuilder.Sql(@"
IF EXISTS (
    SELECT 1
    FROM [JoinRequests]
    WHERE [Status] = 1
    GROUP BY [TeamId], [PlayerId]
    HAVING COUNT(*) > 1
)
BEGIN
    RAISERROR('Cannot apply migration EnforceUniquePendingJoinRequest: duplicate pending JoinRequest rows exist for one or more (TeamId, PlayerId) pairs. Resolve the duplicate pending join requests before re-running this migration.', 16, 1);
    RETURN;
END
");

            migrationBuilder.DropIndex(
                name: "IX_JoinRequests_TeamId_PlayerId",
                table: "JoinRequests");

            migrationBuilder.CreateIndex(
                name: "UX_JoinRequests_TeamId_PlayerId_Pending",
                table: "JoinRequests",
                columns: new[] { "TeamId", "PlayerId" },
                unique: true,
                filter: "[Status] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_JoinRequests_TeamId_PlayerId_Pending",
                table: "JoinRequests");

            migrationBuilder.CreateIndex(
                name: "IX_JoinRequests_TeamId_PlayerId",
                table: "JoinRequests",
                columns: new[] { "TeamId", "PlayerId" });
        }
    }
}
