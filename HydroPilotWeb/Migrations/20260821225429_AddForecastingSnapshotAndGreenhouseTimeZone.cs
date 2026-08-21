using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddForecastingSnapshotAndGreenhouseTimeZone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "AsOfDate",
                table: "Predictions",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CoveragePercent",
                table: "Predictions",
                type: "decimal(5,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DataSource",
                table: "Predictions",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "Greenhouses",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Predictions_LotId_AsOfDate_ModelVersion",
                table: "Predictions",
                columns: new[] { "LotId", "AsOfDate", "ModelVersion" },
                unique: true,
                filter: "[AsOfDate] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Predictions_LotId_AsOfDate_ModelVersion",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "AsOfDate",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "CoveragePercent",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "DataSource",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "Greenhouses");
        }
    }
}
