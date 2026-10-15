using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddLotProductionGoals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "BabyLeafGoalPercent",
                table: "Lots",
                type: "decimal(5,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxLossPercent",
                table: "Lots",
                type: "decimal(5,2)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BabyLeafGoalPercent",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "MaxLossPercent",
                table: "Lots");
        }
    }
}
