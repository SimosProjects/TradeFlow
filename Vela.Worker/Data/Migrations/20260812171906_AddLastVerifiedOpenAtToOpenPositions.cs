using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vela.Worker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLastVerifiedOpenAtToOpenPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_verified_open_at",
                table: "open_positions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_verified_open_at",
                table: "open_positions");
        }
    }
}
