using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeamBuilder.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVacancyOutboxSubscriptionsAndNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InAppNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    OccurrenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RosterRequirementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InAppNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InAppNotifications_Events_OccurrenceId",
                        column: x => x.OccurrenceId,
                        principalTable: "Events",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_InAppNotifications_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OccurrenceRosterSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurrenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RosterRequirementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OccurrenceRosterSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OccurrenceRosterSubscriptions_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OccurrenceRosterSubscriptions_RosterRequirements_RosterRequirementId_OccurrenceId",
                        columns: x => new { x.RosterRequirementId, x.OccurrenceId },
                        principalTable: "RosterRequirements",
                        principalColumns: new[] { "Id", "OccurrenceId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    AggregateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DeduplicationKey = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LockOwner = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: true),
                    LockExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                    table.CheckConstraint("CK_OutboxMessages_AttemptCount_NonNegative", "[AttemptCount] >= 0");
                    table.CheckConstraint("CK_OutboxMessages_Status_Range", "[Status] >= 1 AND [Status] <= 4");
                });

            migrationBuilder.CreateIndex(
                name: "IX_InAppNotifications_OccurrenceId",
                table: "InAppNotifications",
                column: "OccurrenceId");

            migrationBuilder.CreateIndex(
                name: "IX_InAppNotifications_PlayerId_CreatedAtUtc_Id",
                table: "InAppNotifications",
                columns: new[] { "PlayerId", "CreatedAtUtc", "Id" },
                descending: new[] { false, true, true })
                .Annotation("SqlServer:Include", new[] { "ReadAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InAppNotifications_PlayerId_Unread",
                table: "InAppNotifications",
                columns: new[] { "PlayerId", "CreatedAtUtc", "Id" },
                descending: new[] { false, true, true },
                filter: "[ReadAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_InAppNotifications_SourceEventId_PlayerId",
                table: "InAppNotifications",
                columns: new[] { "SourceEventId", "PlayerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OccurrenceRosterSubscriptions_RosterRequirementId_OccurrenceId",
                table: "OccurrenceRosterSubscriptions",
                columns: new[] { "RosterRequirementId", "OccurrenceId" })
                .Annotation("SqlServer:Include", new[] { "PlayerId" });

            migrationBuilder.CreateIndex(
                name: "UX_OccurrenceRosterSubscriptions_PlayerId_OccurrenceId_RosterRequirementId",
                table: "OccurrenceRosterSubscriptions",
                columns: new[] { "PlayerId", "OccurrenceId", "RosterRequirementId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Pending_NextAttemptAtUtc",
                table: "OutboxMessages",
                columns: new[] { "NextAttemptAtUtc", "CreatedAtUtc" },
                filter: "[Status] = 1")
                .Annotation("SqlServer:Include", new[] { "AttemptCount" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Processing_LockExpiresAtUtc",
                table: "OutboxMessages",
                column: "LockExpiresAtUtc",
                filter: "[Status] = 2")
                .Annotation("SqlServer:Include", new[] { "AttemptCount" });

            migrationBuilder.CreateIndex(
                name: "UX_OutboxMessages_DeduplicationKey",
                table: "OutboxMessages",
                column: "DeduplicationKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InAppNotifications");

            migrationBuilder.DropTable(
                name: "OccurrenceRosterSubscriptions");

            migrationBuilder.DropTable(
                name: "OutboxMessages");
        }
    }
}
