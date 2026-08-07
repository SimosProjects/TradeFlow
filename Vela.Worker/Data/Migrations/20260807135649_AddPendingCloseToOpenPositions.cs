using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vela.Worker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingCloseToOpenPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pending_close_outcome",
                table: "open_positions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pending_close_since",
                table: "open_positions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pending_close_outcome",
                table: "open_positions");

            migrationBuilder.DropColumn(
                name: "pending_close_since",
                table: "open_positions");
        }
    }
}
