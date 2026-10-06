using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeamBuilder.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEventSeriesMaterializationCheckpoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No backfill. A correct local checkpoint needs the series' IANA time zone and the
            // DST rules applied in the application; SQL Server would have to guess (or use its
            // own machine zone). Existing series stay NULL ("checkpoint unknown") and the
            // materializer reconciles them idempotently on its first pass.
            migrationBuilder.AddColumn<DateOnly>(
                name: "MaterializedThroughLocalDate",
                table: "EventSeries",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaterializedThroughLocalDate",
                table: "EventSeries");
        }
    }
}
