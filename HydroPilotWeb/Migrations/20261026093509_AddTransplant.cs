using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <summary>
    /// Trasplante a Adultas (2026-10-26): los circuitos pasan a tener tipo (denso / de Adultas) y grilla de huecos; cada
    /// planta sabe en qué circuito está y si se trasplantó; el lote guarda el costo del trasplante por planta y el pico
    /// de aptas a Baby Leaf (óptimo aprendido); la evaluación guarda el tamaño respecto del ideal. Los circuitos que ya
    /// existen quedan densos con la grilla de su último lote, y las plantas, en el circuito de su lote.
    /// </summary>
    public partial class AddTransplant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Plants_LotId_Row_Column",
                table: "Plants");

            migrationBuilder.AddColumn<int>(
                name: "CircuitId",
                table: "Plants",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "TransplantedOn",
                table: "Plants",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BabyLeafPeakGdd",
                table: "Lots",
                type: "decimal(8,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TransplantCostPerPlant",
                table: "Lots",
                type: "decimal(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GridColumns",
                table: "Circuits",
                type: "int",
                nullable: false,
                defaultValue: 20);

            migrationBuilder.AddColumn<int>(
                name: "GridRows",
                table: "Circuits",
                type: "int",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Circuits",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "DENSO");

            migrationBuilder.AddColumn<decimal>(
                name: "SizeRatio",
                table: "BabyLeafEvaluations",
                type: "decimal(6,3)",
                nullable: true);

            migrationBuilder.Sql(@"
UPDATE p SET CircuitId = l.CircuitId
FROM Plants p
JOIN Lots l ON l.Id = p.LotId
WHERE l.CircuitId IS NOT NULL;

UPDATE c SET GridRows = x.GridRows, GridColumns = x.GridColumns
FROM Circuits c
CROSS APPLY (SELECT TOP 1 l.GridRows, l.GridColumns
             FROM Lots l
             WHERE l.CircuitId = c.Id AND l.GridRows > 0 AND l.GridColumns > 0
             ORDER BY l.SowingDate DESC, l.Id DESC) x;");

            migrationBuilder.CreateIndex(
                name: "IX_Plants_CircuitId",
                table: "Plants",
                column: "CircuitId");

            migrationBuilder.CreateIndex(
                name: "IX_Plants_LotId_CircuitId_Row_Column",
                table: "Plants",
                columns: new[] { "LotId", "CircuitId", "Row", "Column" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Plants_Circuits_CircuitId",
                table: "Plants",
                column: "CircuitId",
                principalTable: "Circuits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Plants_Circuits_CircuitId",
                table: "Plants");

            migrationBuilder.DropIndex(
                name: "IX_Plants_CircuitId",
                table: "Plants");

            migrationBuilder.DropIndex(
                name: "IX_Plants_LotId_CircuitId_Row_Column",
                table: "Plants");

            migrationBuilder.DropColumn(
                name: "CircuitId",
                table: "Plants");

            migrationBuilder.DropColumn(
                name: "TransplantedOn",
                table: "Plants");

            migrationBuilder.DropColumn(
                name: "BabyLeafPeakGdd",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "TransplantCostPerPlant",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "GridColumns",
                table: "Circuits");

            migrationBuilder.DropColumn(
                name: "GridRows",
                table: "Circuits");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Circuits");

            migrationBuilder.DropColumn(
                name: "SizeRatio",
                table: "BabyLeafEvaluations");

            migrationBuilder.CreateIndex(
                name: "IX_Plants_LotId_Row_Column",
                table: "Plants",
                columns: new[] { "LotId", "Row", "Column" },
                unique: true);
        }
    }
}
