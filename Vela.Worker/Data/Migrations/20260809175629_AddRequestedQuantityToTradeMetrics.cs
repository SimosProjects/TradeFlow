using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vela.Worker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRequestedQuantityToTradeMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "requested_quantity",
                table: "trade_metrics",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "requested_quantity",
                table: "trade_metrics");
        }
    }
}
