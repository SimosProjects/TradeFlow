using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Vela.Worker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderRejections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "order_rejections",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    alert_id = table.Column<string>(type: "text", nullable: true),
                    trader_name = table.Column<string>(type: "text", nullable: true),
                    symbol = table.Column<string>(type: "text", nullable: true),
                    trade_type = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: false),
                    requested_quantity = table.Column<int>(type: "integer", nullable: false),
                    requested_price = table.Column<decimal>(type: "numeric", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_rejections", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_order_rejections_created_at",
                table: "order_rejections",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "idx_order_rejections_symbol",
                table: "order_rejections",
                column: "symbol");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_rejections");
        }
    }
}
