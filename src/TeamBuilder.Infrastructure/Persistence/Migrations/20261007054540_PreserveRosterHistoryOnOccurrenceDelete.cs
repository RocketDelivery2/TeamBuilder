using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeamBuilder.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreserveRosterHistoryOnOccurrenceDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RosterAssignments_Events_OccurrenceId",
                table: "RosterAssignments");

            migrationBuilder.AddForeignKey(
                name: "FK_RosterAssignments_Events_OccurrenceId",
                table: "RosterAssignments",
                column: "OccurrenceId",
                principalTable: "Events",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RosterAssignments_Events_OccurrenceId",
                table: "RosterAssignments");

            migrationBuilder.AddForeignKey(
                name: "FK_RosterAssignments_Events_OccurrenceId",
                table: "RosterAssignments",
                column: "OccurrenceId",
                principalTable: "Events",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
