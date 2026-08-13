using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddForecasting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CropTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    GddTarget = table.Column<decimal>(type: "decimal(8,2)", nullable: false),
                    BaseTemperature = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    OptimalPhMin = table.Column<decimal>(type: "decimal(4,2)", nullable: true),
                    OptimalPhMax = table.Column<decimal>(type: "decimal(4,2)", nullable: true),
                    OptimalEcMin = table.Column<decimal>(type: "decimal(6,2)", nullable: true),
                    OptimalEcMax = table.Column<decimal>(type: "decimal(6,2)", nullable: true),
                    EstimatedDaysToHarvest = table.Column<int>(type: "int", nullable: true),
                    YieldPerM2 = table.Column<decimal>(type: "decimal(8,2)", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CropTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LotStatuses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LotStatuses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Lots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GreenhouseId = table.Column<int>(type: "int", nullable: false),
                    CropTypeId = table.Column<int>(type: "int", nullable: false),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    SowingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PlantedAreaM2 = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Lots_CropTypes_CropTypeId",
                        column: x => x.CropTypeId,
                        principalTable: "CropTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Lots_Greenhouses_GreenhouseId",
                        column: x => x.GreenhouseId,
                        principalTable: "Greenhouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Lots_LotStatuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "LotStatuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Predictions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LotId = table.Column<int>(type: "int", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EstimatedHarvestDate = table.Column<DateOnly>(type: "date", nullable: true),
                    AccumulatedGdd = table.Column<decimal>(type: "decimal(8,2)", nullable: false),
                    EstimatedYield = table.Column<decimal>(type: "decimal(8,2)", nullable: true),
                    ModelVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Predictions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Predictions_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SensorReadings_LotId",
                table: "SensorReadings",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_Lots_CropTypeId",
                table: "Lots",
                column: "CropTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Lots_GreenhouseId",
                table: "Lots",
                column: "GreenhouseId");

            migrationBuilder.CreateIndex(
                name: "IX_Lots_StatusId",
                table: "Lots",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_LotStatuses_Name",
                table: "LotStatuses",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Predictions_GeneratedAt",
                table: "Predictions",
                column: "GeneratedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Predictions_LotId",
                table: "Predictions",
                column: "LotId");

            migrationBuilder.AddForeignKey(
                name: "FK_SensorReadings_Lots_LotId",
                table: "SensorReadings",
                column: "LotId",
                principalTable: "Lots",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SensorReadings_Lots_LotId",
                table: "SensorReadings");

            migrationBuilder.DropTable(
                name: "Predictions");

            migrationBuilder.DropTable(
                name: "Lots");

            migrationBuilder.DropTable(
                name: "CropTypes");

            migrationBuilder.DropTable(
                name: "LotStatuses");

            migrationBuilder.DropIndex(
                name: "IX_SensorReadings_LotId",
                table: "SensorReadings");
        }
    }
}
