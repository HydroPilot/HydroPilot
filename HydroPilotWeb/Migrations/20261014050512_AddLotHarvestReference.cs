using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddLotHarvestReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "HarvestBabyLeafGramsPerPlant",
                table: "Lots",
                type: "decimal(7,1)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HarvestBabyLeafPricePerKg",
                table: "Lots",
                type: "decimal(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HarvestConventionalGramsPerPlant",
                table: "Lots",
                type: "decimal(7,1)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HarvestConventionalPricePerKg",
                table: "Lots",
                type: "decimal(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HarvestPriceCurrency",
                table: "Lots",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HarvestBabyLeafGramsPerPlant",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "HarvestBabyLeafPricePerKg",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "HarvestConventionalGramsPerPlant",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "HarvestConventionalPricePerKg",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "HarvestPriceCurrency",
                table: "Lots");
        }
    }
}
