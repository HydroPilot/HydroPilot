using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddLotCycleOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BabyLeafAptAtOptimum",
                table: "Lots",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CycleAverageDailyGdd",
                table: "Lots",
                type: "decimal(6,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EarlyLossPlants",
                table: "Lots",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LateLossPlants",
                table: "Lots",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BabyLeafAptAtOptimum",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "CycleAverageDailyGdd",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "EarlyLossPlants",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "LateLossPlants",
                table: "Lots");
        }
    }
}
