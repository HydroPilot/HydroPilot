using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddLotHarvestData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "ActualHarvestDate",
                table: "Lots",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ActualYieldKg",
                table: "Lots",
                type: "decimal(10,2)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActualHarvestDate",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "ActualYieldKg",
                table: "Lots");
        }
    }
}
