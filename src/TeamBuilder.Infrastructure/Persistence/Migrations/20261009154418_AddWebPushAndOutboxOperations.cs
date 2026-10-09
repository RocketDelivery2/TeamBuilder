using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeamBuilder.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWebPushAndOutboxOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastReplayedAtUtc",
                table: "OutboxMessages",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PriorAttemptCount",
                table: "OutboxMessages",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReplayCount",
                table: "OutboxMessages",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "OpenedAtUtc",
                table: "InAppNotifications",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PushDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InAppNotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PushSubscriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LockOwner = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: true),
                    LockExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SourceOccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastStatusCode = table.Column<int>(type: "int", nullable: true),
                    LastError = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushDeliveries", x => x.Id);
                    table.CheckConstraint("CK_PushDeliveries_Status_Range", "[Status] >= 1 AND [Status] <= 5");
                    table.ForeignKey(
                        name: "FK_PushDeliveries_InAppNotifications_InAppNotificationId",
                        column: x => x.InAppNotificationId,
                        principalTable: "InAppNotifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PushSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Endpoint = table.Column<string>(type: "varchar(2048)", unicode: false, maxLength: 2048, nullable: false),
                    EndpointHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    P256dh = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    Auth = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    UserAgentFamily = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    LastSeenAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    FailureCount = table.Column<int>(type: "int", nullable: false),
                    DisabledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DisabledReason = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushSubscriptions", x => x.Id);
                    table.CheckConstraint("CK_PushSubscriptions_FailureCount_NonNegative", "[FailureCount] >= 0");
                    table.ForeignKey(
                        name: "FK_PushSubscriptions_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Completed_ProcessedAtUtc",
                table: "OutboxMessages",
                column: "ProcessedAtUtc",
                filter: "[Status] = 3");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Failed_ProcessedAtUtc",
                table: "OutboxMessages",
                column: "ProcessedAtUtc",
                filter: "[Status] = 4")
                .Annotation("SqlServer:Include", new[] { "Type", "AggregateId", "AttemptCount", "ReplayCount" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_OutboxMessages_ReplayCounts_NonNegative",
                table: "OutboxMessages",
                sql: "[ReplayCount] >= 0 AND [PriorAttemptCount] >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_PushDeliveries_Pending_NextAttemptAtUtc",
                table: "PushDeliveries",
                columns: new[] { "NextAttemptAtUtc", "CreatedAtUtc" },
                filter: "[Status] = 1")
                .Annotation("SqlServer:Include", new[] { "AttemptCount" });

            migrationBuilder.CreateIndex(
                name: "IX_PushDeliveries_Sending_LockExpiresAtUtc",
                table: "PushDeliveries",
                column: "LockExpiresAtUtc",
                filter: "[Status] = 2")
                .Annotation("SqlServer:Include", new[] { "AttemptCount" });

            migrationBuilder.CreateIndex(
                name: "IX_PushDeliveries_Terminal_CompletedAtUtc",
                table: "PushDeliveries",
                column: "CompletedAtUtc",
                filter: "[Status] >= 3");

            migrationBuilder.CreateIndex(
                name: "UX_PushDeliveries_InAppNotificationId_PushSubscriptionId",
                table: "PushDeliveries",
                columns: new[] { "InAppNotificationId", "PushSubscriptionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_PlayerId_Active",
                table: "PushSubscriptions",
                columns: new[] { "PlayerId", "LastSeenAtUtc" },
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_PushSubscriptions_EndpointHash",
                table: "PushSubscriptions",
                column: "EndpointHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PushDeliveries");

            migrationBuilder.DropTable(
                name: "PushSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_Completed_ProcessedAtUtc",
                table: "OutboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_Failed_ProcessedAtUtc",
                table: "OutboxMessages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OutboxMessages_ReplayCounts_NonNegative",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "LastReplayedAtUtc",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "PriorAttemptCount",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "ReplayCount",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "OpenedAtUtc",
                table: "InAppNotifications");
        }
    }
}
