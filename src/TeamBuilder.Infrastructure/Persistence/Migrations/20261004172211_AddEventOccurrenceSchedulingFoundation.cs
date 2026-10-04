using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeamBuilder.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Event occurrence scheduling foundation. Existing events already are occurrences, so the
    /// Events table is evolved in place (no copy, no second table): EventDateUtc is renamed to
    /// ScheduledStartUtc and Location to LegacyLocation, preserving every row id, value and
    /// RosterEntries.EventId link. The new Events columns start empty for existing rows
    /// (SeriesId/VenueId/ScheduledEndUtc/OccurrenceIndex NULL, IsDetached false): no venues,
    /// time zones, end times or series are invented. Venues and EventSeries are new tables.
    ///
    /// Down renames the two columns back (data intact) and drops the new columns and tables,
    /// discarding any Venue/EventSeries rows and occurrence links written after Up.
    /// </summary>
    public partial class AddEventOccurrenceSchedulingFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Location",
                table: "Events",
                newName: "LegacyLocation");

            migrationBuilder.RenameColumn(
                name: "EventDateUtc",
                table: "Events",
                newName: "ScheduledStartUtc");

            migrationBuilder.RenameIndex(
                name: "IX_Events_EventDateUtc",
                table: "Events",
                newName: "IX_Events_ScheduledStartUtc");

            migrationBuilder.AddColumn<bool>(
                name: "IsDetached",
                table: "Events",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "OccurrenceIndex",
                table: "Events",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ScheduledEndUtc",
                table: "Events",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SeriesId",
                table: "Events",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VenueId",
                table: "Events",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Venues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AddressLine1 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AddressLine2 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    StateOrProvince = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CountryCode = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    Latitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    Longitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    TimeZoneId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    VenueType = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Venues", x => x.Id);
                    table.CheckConstraint("CK_Venues_Latitude_Range", "[Latitude] >= -90 AND [Latitude] <= 90");
                    table.CheckConstraint("CK_Venues_Longitude_Range", "[Longitude] >= -180 AND [Longitude] <= 180");
                });

            migrationBuilder.CreateTable(
                name: "EventSeries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Tags = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HostId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VenueId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LocalStartTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    DurationMinutes = table.Column<int>(type: "int", nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RecurrenceRule = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    SeriesStartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SeriesEndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventSeries", x => x.Id);
                    table.CheckConstraint("CK_EventSeries_DurationMinutes_Range", "[DurationMinutes] > 0 AND [DurationMinutes] <= 10080");
                    table.ForeignKey(
                        name: "FK_EventSeries_Players_HostId",
                        column: x => x.HostId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_EventSeries_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_EventSeries_Venues_VenueId",
                        column: x => x.VenueId,
                        principalTable: "Venues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Events_SeriesId",
                table: "Events",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_VenueId",
                table: "Events",
                column: "VenueId");

            migrationBuilder.CreateIndex(
                name: "IX_EventSeries_HostId",
                table: "EventSeries",
                column: "HostId");

            migrationBuilder.CreateIndex(
                name: "IX_EventSeries_Status",
                table: "EventSeries",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_EventSeries_TeamId",
                table: "EventSeries",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_EventSeries_VenueId",
                table: "EventSeries",
                column: "VenueId");

            migrationBuilder.AddForeignKey(
                name: "FK_Events_EventSeries_SeriesId",
                table: "Events",
                column: "SeriesId",
                principalTable: "EventSeries",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Events_Venues_VenueId",
                table: "Events",
                column: "VenueId",
                principalTable: "Venues",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Events_EventSeries_SeriesId",
                table: "Events");

            migrationBuilder.DropForeignKey(
                name: "FK_Events_Venues_VenueId",
                table: "Events");

            migrationBuilder.DropTable(
                name: "EventSeries");

            migrationBuilder.DropTable(
                name: "Venues");

            migrationBuilder.DropIndex(
                name: "IX_Events_SeriesId",
                table: "Events");

            migrationBuilder.DropIndex(
                name: "IX_Events_VenueId",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "IsDetached",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "OccurrenceIndex",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "ScheduledEndUtc",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "SeriesId",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "VenueId",
                table: "Events");

            migrationBuilder.RenameColumn(
                name: "ScheduledStartUtc",
                table: "Events",
                newName: "EventDateUtc");

            migrationBuilder.RenameColumn(
                name: "LegacyLocation",
                table: "Events",
                newName: "Location");

            migrationBuilder.RenameIndex(
                name: "IX_Events_ScheduledStartUtc",
                table: "Events",
                newName: "IX_Events_EventDateUtc");
        }
    }
}
