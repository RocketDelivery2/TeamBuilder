using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeamBuilder.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEventRosterRequirementsAndAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RosterRequirements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurrenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DisplayPosition = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SourceRoleLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RequiredCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RosterRequirements", x => x.Id);
                    table.UniqueConstraint("AK_RosterRequirements_Id_OccurrenceId", x => new { x.Id, x.OccurrenceId });
                    table.CheckConstraint("CK_RosterRequirements_RequiredCount_Range", "[RequiredCount] >= 1 AND [RequiredCount] <= 100000");
                    table.CheckConstraint("CK_RosterRequirements_RoleCode_NotEmpty", "[RoleCode] <> N''");
                    table.ForeignKey(
                        name: "FK_RosterRequirements_Events_OccurrenceId",
                        column: x => x.OccurrenceId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RosterAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurrenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequirementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RoleCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SourceRoleLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    ReservedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CheckedInAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ActivatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DepartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExitReason = table.Column<int>(type: "int", nullable: true),
                    ReplacedAssignmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RosterAssignments", x => x.Id);
                    table.UniqueConstraint("AK_RosterAssignments_Id_OccurrenceId", x => new { x.Id, x.OccurrenceId });
                    table.CheckConstraint("CK_RosterAssignments_ExitReason_Range", "[ExitReason] IS NULL OR ([ExitReason] >= 1 AND [ExitReason] <= 5)");
                    table.CheckConstraint("CK_RosterAssignments_ReplacedAssignmentId_NotSelf", "[ReplacedAssignmentId] IS NULL OR [ReplacedAssignmentId] <> [Id]");
                    table.CheckConstraint("CK_RosterAssignments_Source_Range", "[Source] >= 1 AND [Source] <= 4");
                    table.CheckConstraint("CK_RosterAssignments_Status_Range", "[Status] >= 1 AND [Status] <= 7");
                    table.ForeignKey(
                        name: "FK_RosterAssignments_Events_OccurrenceId",
                        column: x => x.OccurrenceId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RosterAssignments_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RosterAssignments_RosterAssignments_ReplacedAssignmentId_OccurrenceId",
                        columns: x => new { x.ReplacedAssignmentId, x.OccurrenceId },
                        principalTable: "RosterAssignments",
                        principalColumns: new[] { "Id", "OccurrenceId" });
                    table.ForeignKey(
                        name: "FK_RosterAssignments_RosterRequirements_RequirementId_OccurrenceId",
                        columns: x => new { x.RequirementId, x.OccurrenceId },
                        principalTable: "RosterRequirements",
                        principalColumns: new[] { "Id", "OccurrenceId" });
                });

            migrationBuilder.CreateIndex(
                name: "IX_RosterAssignments_OccurrenceId_Status",
                table: "RosterAssignments",
                columns: new[] { "OccurrenceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RosterAssignments_PlayerId",
                table: "RosterAssignments",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_RosterAssignments_ReplacedAssignmentId_OccurrenceId",
                table: "RosterAssignments",
                columns: new[] { "ReplacedAssignmentId", "OccurrenceId" });

            migrationBuilder.CreateIndex(
                name: "IX_RosterAssignments_RequirementId_OccurrenceId",
                table: "RosterAssignments",
                columns: new[] { "RequirementId", "OccurrenceId" });

            migrationBuilder.CreateIndex(
                name: "UX_RosterAssignments_OccurrenceId_PlayerId_Supply",
                table: "RosterAssignments",
                columns: new[] { "OccurrenceId", "PlayerId" },
                unique: true,
                filter: "[Status] IN (1, 2, 3, 4)");

            migrationBuilder.CreateIndex(
                name: "UX_RosterRequirements_OccurrenceId_RoleCode",
                table: "RosterRequirements",
                columns: new[] { "OccurrenceId", "RoleCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RosterAssignments");

            migrationBuilder.DropTable(
                name: "RosterRequirements");
        }
    }
}
