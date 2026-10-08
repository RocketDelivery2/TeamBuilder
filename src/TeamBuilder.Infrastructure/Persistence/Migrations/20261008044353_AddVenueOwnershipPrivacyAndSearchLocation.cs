using System;
using Microsoft.EntityFrameworkCore.Migrations;
using TeamBuilder.Infrastructure.Data.Configurations;

#nullable disable

namespace TeamBuilder.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVenueOwnershipPrivacyAndSearchLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByPlayerId",
                table: "Venues",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PrivacyLevel",
                table: "Venues",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_Venues_CreatedByPlayerId",
                table: "Venues",
                column: "CreatedByPlayerId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Venues_PrivacyLevel_Range",
                table: "Venues",
                sql: "[PrivacyLevel] >= 1 AND [PrivacyLevel] <= 2");

            migrationBuilder.CreateIndex(
                name: "IX_Events_VenueId_Status_ScheduledStartUtc",
                table: "Events",
                columns: new[] { "VenueId", "Status", "ScheduledStartUtc" })
                .Annotation("SqlServer:Include", new[] { "Category" });

            // The new index leads with VenueId, so it also serves the venue foreign key.
            migrationBuilder.DropIndex(
                name: "IX_Events_VenueId",
                table: "Events");

            migrationBuilder.AddForeignKey(
                name: "FK_Venues_Players_CreatedByPlayerId",
                table: "Venues",
                column: "CreatedByPlayerId",
                principalTable: "Players",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Discovery's spatial representation, outside the EF model (see
            // VenueConfiguration.SearchLocationColumnSql). Persisted and computed from the
            // existing decimals: no coordinates are fabricated, rows without both coordinates
            // and virtual venues get NULL, and every later insert keeps it in step atomically.
            migrationBuilder.Sql(
                $"ALTER TABLE [Venues] ADD [{VenueConfiguration.SearchLocationColumnName}] AS ({VenueConfiguration.SearchLocationColumnSql}) PERSISTED;");
            migrationBuilder.Sql(
                $"CREATE SPATIAL INDEX [{VenueConfiguration.SearchLocationIndexName}] ON [Venues] ([{VenueConfiguration.SearchLocationColumnName}]) USING GEOGRAPHY_AUTO_GRID;");

            // Covering for discovery's openOnly supply count per requirement.
            migrationBuilder.DropIndex(
                name: "IX_RosterAssignments_RequirementId_OccurrenceId",
                table: "RosterAssignments");

            migrationBuilder.CreateIndex(
                name: "IX_RosterAssignments_RequirementId_OccurrenceId",
                table: "RosterAssignments",
                columns: new[] { "RequirementId", "OccurrenceId" })
                .Annotation("SqlServer:Include", new[] { "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RosterAssignments_RequirementId_OccurrenceId",
                table: "RosterAssignments");

            migrationBuilder.CreateIndex(
                name: "IX_RosterAssignments_RequirementId_OccurrenceId",
                table: "RosterAssignments",
                columns: new[] { "RequirementId", "OccurrenceId" });

            migrationBuilder.Sql(
                $"DROP INDEX [{VenueConfiguration.SearchLocationIndexName}] ON [Venues];");
            migrationBuilder.Sql(
                $"ALTER TABLE [Venues] DROP COLUMN [{VenueConfiguration.SearchLocationColumnName}];");

            migrationBuilder.DropForeignKey(
                name: "FK_Venues_Players_CreatedByPlayerId",
                table: "Venues");

            migrationBuilder.DropIndex(
                name: "IX_Venues_CreatedByPlayerId",
                table: "Venues");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Venues_PrivacyLevel_Range",
                table: "Venues");

            migrationBuilder.CreateIndex(
                name: "IX_Events_VenueId",
                table: "Events",
                column: "VenueId");

            migrationBuilder.DropIndex(
                name: "IX_Events_VenueId_Status_ScheduledStartUtc",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "CreatedByPlayerId",
                table: "Venues");

            migrationBuilder.DropColumn(
                name: "PrivacyLevel",
                table: "Venues");
        }
    }
}
