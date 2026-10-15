using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddSensorReadingTimeIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SensorReadings_LotId_ObservedAtUtc",
                table: "SensorReadings",
                columns: new[] { "LotId", "ObservedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SensorReadings_SensorId_ObservedAtUtc",
                table: "SensorReadings",
                columns: new[] { "SensorId", "ObservedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SensorReadings_LotId_ObservedAtUtc",
                table: "SensorReadings");

            migrationBuilder.DropIndex(
                name: "IX_SensorReadings_SensorId_ObservedAtUtc",
                table: "SensorReadings");
        }
    }
}
