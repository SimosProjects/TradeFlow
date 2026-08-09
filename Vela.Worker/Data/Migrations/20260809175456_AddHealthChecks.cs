using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Vela.Worker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHealthChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "health_checks",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    worker_status = table.Column<string>(type: "text", nullable: false),
                    ibkr_status = table.Column<string>(type: "text", nullable: false),
                    postgres_status = table.Column<string>(type: "text", nullable: false),
                    xtrades_status = table.Column<string>(type: "text", nullable: false),
                    signalr_status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_health_checks", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_health_checks_checked_at",
                table: "health_checks",
                column: "checked_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "health_checks");
        }
    }
}
