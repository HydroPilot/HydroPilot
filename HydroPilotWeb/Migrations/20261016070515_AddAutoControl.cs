using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "SourceWaterEc",
                table: "IotNodes",
                type: "decimal(4,2)",
                nullable: false,
                defaultValue: 0.2m);

            migrationBuilder.AddColumn<decimal>(
                name: "EffectPerUnit",
                table: "Actuators",
                type: "decimal(10,4)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EffectSamples",
                table: "Actuators",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "EffectEvaluatedAtUtc",
                table: "ActuatorCommands",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Effective",
                table: "ActuatorCommands",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExpectedEffect",
                table: "ActuatorCommands",
                type: "decimal(8,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ObservedEffect",
                table: "ActuatorCommands",
                type: "decimal(8,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Strength",
                table: "ActuatorCommands",
                type: "decimal(4,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ValueAfter",
                table: "ActuatorCommands",
                type: "decimal(12,4)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ControlPauses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NodeId = table.Column<int>(type: "int", nullable: false),
                    Parameter = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    PausedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ResumedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResumedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ControlPauses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ControlPauses_IotNodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "IotNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ControlPauses_NodeId_Parameter_PausedAtUtc",
                table: "ControlPauses",
                columns: new[] { "NodeId", "Parameter", "PausedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ControlPauses_Open",
                table: "ControlPauses",
                columns: new[] { "NodeId", "Parameter" },
                unique: true,
                filter: "[ResumedAtUtc] IS NULL");

            // Bombas creadas en la fase 1: arrancan con el efecto de fábrica (ControlContract.DefaultActuators).
            migrationBuilder.Sql(@"
UPDATE Actuators SET EffectPerUnit = 2.5 WHERE EffectPerUnit IS NULL AND Kind IN ('ph-up', 'ph-down');
UPDATE Actuators SET EffectPerUnit = 0.3 WHERE EffectPerUnit IS NULL AND Kind = 'nutrient';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ControlPauses");

            migrationBuilder.DropColumn(
                name: "SourceWaterEc",
                table: "IotNodes");

            migrationBuilder.DropColumn(
                name: "EffectPerUnit",
                table: "Actuators");

            migrationBuilder.DropColumn(
                name: "EffectSamples",
                table: "Actuators");

            migrationBuilder.DropColumn(
                name: "EffectEvaluatedAtUtc",
                table: "ActuatorCommands");

            migrationBuilder.DropColumn(
                name: "Effective",
                table: "ActuatorCommands");

            migrationBuilder.DropColumn(
                name: "ExpectedEffect",
                table: "ActuatorCommands");

            migrationBuilder.DropColumn(
                name: "ObservedEffect",
                table: "ActuatorCommands");

            migrationBuilder.DropColumn(
                name: "Strength",
                table: "ActuatorCommands");

            migrationBuilder.DropColumn(
                name: "ValueAfter",
                table: "ActuatorCommands");
        }
    }
}
