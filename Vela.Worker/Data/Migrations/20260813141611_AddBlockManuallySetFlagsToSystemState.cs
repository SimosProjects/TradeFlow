using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vela.Worker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBlockManuallySetFlagsToSystemState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BlockCallsManuallySet",
                table: "system_state",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "BlockHighManuallySet",
                table: "system_state",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "BlockLottoManuallySet",
                table: "system_state",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlockCallsManuallySet",
                table: "system_state");

            migrationBuilder.DropColumn(
                name: "BlockHighManuallySet",
                table: "system_state");

            migrationBuilder.DropColumn(
                name: "BlockLottoManuallySet",
                table: "system_state");
        }
    }
}
