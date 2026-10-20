using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeamBuilder.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegrationApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Events_HostId",
                table: "Events");

            migrationBuilder.CreateTable(
                name: "IntegrationApiKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    KeyPrefix = table.Column<string>(type: "char(12)", unicode: false, fixedLength: true, maxLength: 12, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SecretHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    Scopes = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastUsedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IntegrationApiKeys_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Events_HostId_ScheduledStartUtc",
                table: "Events",
                columns: new[] { "HostId", "ScheduledStartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationApiKeys_PlayerId",
                table: "IntegrationApiKeys",
                columns: new[] { "PlayerId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_IntegrationApiKeys_KeyPrefix",
                table: "IntegrationApiKeys",
                column: "KeyPrefix",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntegrationApiKeys");

            migrationBuilder.DropIndex(
                name: "IX_Events_HostId_ScheduledStartUtc",
                table: "Events");

            migrationBuilder.CreateIndex(
                name: "IX_Events_HostId",
                table: "Events",
                column: "HostId");
        }
    }
}
