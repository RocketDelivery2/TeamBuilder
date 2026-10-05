using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeamBuilder.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRecurringEventSeries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No public series API existed before this migration, so existing rows (if any were
            // inserted internally) take the same default ceiling as one-off events.
            migrationBuilder.AddColumn<int>(
                name: "MaxParticipants",
                table: "EventSeries",
                type: "int",
                nullable: false,
                defaultValue: 50);

            migrationBuilder.AddCheckConstraint(
                name: "CK_EventSeries_MaxParticipants_Range",
                table: "EventSeries",
                sql: "[MaxParticipants] >= 1 AND [MaxParticipants] <= 100000");

            migrationBuilder.CreateIndex(
                name: "UX_Events_SeriesId_ScheduledStartUtc",
                table: "Events",
                columns: new[] { "SeriesId", "ScheduledStartUtc" },
                unique: true,
                filter: "[SeriesId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_EventSeries_MaxParticipants_Range",
                table: "EventSeries");

            migrationBuilder.DropIndex(
                name: "UX_Events_SeriesId_ScheduledStartUtc",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "MaxParticipants",
                table: "EventSeries");
        }
    }
}
