using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <summary>
    /// Circuitos (2026-10-26): un invernadero tiene una Pi que maneja varios circuitos (tanque + canales), cada uno con
    /// sus sensores de pH y CE, sus 4 bombas y un solo lote en curso; temperatura y humedad son del invernadero.
    /// Convierte los datos existentes sin perderlos: cada nodo pasa a ser un circuito ("Circuito N") con el control
    /// del tanque que tenía; sus sensores de pH/CE y sus bombas van a ese circuito; los de temperatura y humedad quedan
    /// del invernadero (si hay más de uno de cada tipo queda activo el más viejo); las asignaciones nodo → lote pasan a
    /// ser circuito → lote (misma tabla, renombrada, así las lecturas conservan su asignación); pausas, dosis, lotes y
    /// visitas toman el circuito de su nodo.
    /// </summary>
    public partial class AddCircuits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- Circuitos: uno por nodo, con el control del tanque del nodo ---
            migrationBuilder.CreateTable(
                name: "Circuits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GreenhouseId = table.Column<int>(type: "int", nullable: false),
                    NodeId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    ControlMode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false, defaultValue: "MANUAL"),
                    ReservoirLiters = table.Column<decimal>(type: "decimal(10,1)", nullable: false, defaultValue: 500m),
                    SourceWaterEc = table.Column<decimal>(type: "decimal(4,2)", nullable: false, defaultValue: 0.2m),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Circuits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Circuits_Greenhouses_GreenhouseId",
                        column: x => x.GreenhouseId,
                        principalTable: "Greenhouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Circuits_IotNodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "IotNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Circuits_GreenhouseId_Name",
                table: "Circuits",
                columns: new[] { "GreenhouseId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Circuits_NodeId",
                table: "Circuits",
                column: "NodeId");

            migrationBuilder.Sql(@"
INSERT INTO Circuits (GreenhouseId, NodeId, Name, ControlMode, ReservoirLiters, SourceWaterEc, IsActive, CreatedAt)
SELECT n.GreenhouseId, n.Id,
       CONCAT(N'Circuito ', ROW_NUMBER() OVER (PARTITION BY n.GreenhouseId ORDER BY n.Id)),
       n.ControlMode, n.ReservoirLiters, n.SourceWaterEc, 1, GETUTCDATE()
FROM IotNodes n;");

            // --- Sensores: pH y CE al circuito de su nodo; temperatura y humedad, del invernadero ---
            migrationBuilder.AddColumn<int>(
                name: "CircuitId",
                table: "Sensors",
                type: "int",
                nullable: true);

            migrationBuilder.Sql(@"
UPDATE s SET CircuitId = c.Id
FROM Sensors s
JOIN SensorTypes t ON t.Id = s.SensorTypeId
JOIN Circuits c ON c.NodeId = s.NodeId
WHERE t.Name IN (N'pH', N'CE');

WITH ambient AS (
    SELECT s.Id, ROW_NUMBER() OVER (PARTITION BY n.GreenhouseId, s.SensorTypeId ORDER BY s.Id) AS rn
    FROM Sensors s
    JOIN IotNodes n ON n.Id = s.NodeId
    JOIN SensorTypes t ON t.Id = s.SensorTypeId
    WHERE s.CircuitId IS NULL AND s.IsActive = 1 AND t.Name IN (N'Temperatura', N'Humedad'))
UPDATE Sensors SET IsActive = 0 WHERE Id IN (SELECT Id FROM ambient WHERE rn > 1);");

            migrationBuilder.CreateIndex(
                name: "IX_Sensors_CircuitId",
                table: "Sensors",
                column: "CircuitId");

            migrationBuilder.AddForeignKey(
                name: "FK_Sensors_Circuits_CircuitId",
                table: "Sensors",
                column: "CircuitId",
                principalTable: "Circuits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // --- Bombas: al circuito de su nodo (una de cada tipo por circuito) ---
            migrationBuilder.AddColumn<int>(
                name: "CircuitId",
                table: "Actuators",
                type: "int",
                nullable: true);

            migrationBuilder.Sql(@"
UPDATE a SET CircuitId = c.Id
FROM Actuators a
JOIN Circuits c ON c.NodeId = a.NodeId;");

            migrationBuilder.AlterColumn<int>(
                name: "CircuitId",
                table: "Actuators",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.DropIndex(
                name: "IX_Actuators_NodeId_Kind",
                table: "Actuators");

            migrationBuilder.CreateIndex(
                name: "IX_Actuators_CircuitId_Kind",
                table: "Actuators",
                columns: new[] { "CircuitId", "Kind" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Actuators_Circuits_CircuitId",
                table: "Actuators",
                column: "CircuitId",
                principalTable: "Circuits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // --- Dosis: el circuito de su bomba ---
            migrationBuilder.AddColumn<int>(
                name: "CircuitId",
                table: "ActuatorCommands",
                type: "int",
                nullable: true);

            migrationBuilder.Sql(@"
UPDATE ac SET CircuitId = a.CircuitId
FROM ActuatorCommands ac
JOIN Actuators a ON a.Id = ac.ActuatorId;");

            migrationBuilder.AlterColumn<int>(
                name: "CircuitId",
                table: "ActuatorCommands",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActuatorCommands_CircuitId_CreatedAtUtc",
                table: "ActuatorCommands",
                columns: new[] { "CircuitId", "CreatedAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_ActuatorCommands_Circuits_CircuitId",
                table: "ActuatorCommands",
                column: "CircuitId",
                principalTable: "Circuits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // --- Pausas del control: del circuito (antes del nodo) ---
            migrationBuilder.DropForeignKey(
                name: "FK_ControlPauses_IotNodes_NodeId",
                table: "ControlPauses");

            migrationBuilder.RenameColumn(
                name: "NodeId",
                table: "ControlPauses",
                newName: "CircuitId");

            migrationBuilder.RenameIndex(
                name: "IX_ControlPauses_NodeId_Parameter_PausedAtUtc",
                table: "ControlPauses",
                newName: "IX_ControlPauses_CircuitId_Parameter_PausedAtUtc");

            migrationBuilder.Sql(@"
UPDATE p SET CircuitId = c.Id
FROM ControlPauses p
JOIN Circuits c ON c.NodeId = p.CircuitId;");

            migrationBuilder.AddForeignKey(
                name: "FK_ControlPauses_Circuits_CircuitId",
                table: "ControlPauses",
                column: "CircuitId",
                principalTable: "Circuits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // --- Asignaciones: nodo → lote pasa a circuito → lote (misma tabla y mismos Id) ---
            migrationBuilder.DropForeignKey(
                name: "FK_SensorReadings_NodeLotAssignments_NodeLotAssignmentId",
                table: "SensorReadings");

            migrationBuilder.DropForeignKey(
                name: "FK_NodeLotAssignments_IotNodes_NodeId",
                table: "NodeLotAssignments");

            migrationBuilder.DropForeignKey(
                name: "FK_NodeLotAssignments_Lots_LotId",
                table: "NodeLotAssignments");

            migrationBuilder.DropIndex(
                name: "IX_NodeLotAssignments_NodeId",
                table: "NodeLotAssignments");

            migrationBuilder.DropIndex(
                name: "IX_NodeLotAssignments_LotId",
                table: "NodeLotAssignments");

            migrationBuilder.RenameTable(
                name: "NodeLotAssignments",
                newName: "CircuitAssignments");

            migrationBuilder.Sql("EXEC sp_rename N'dbo.PK_NodeLotAssignments', N'PK_CircuitAssignments', N'OBJECT';");

            migrationBuilder.RenameColumn(
                name: "NodeId",
                table: "CircuitAssignments",
                newName: "CircuitId");

            migrationBuilder.Sql(@"
UPDATE a SET CircuitId = c.Id
FROM CircuitAssignments a
JOIN Circuits c ON c.NodeId = a.CircuitId;");

            migrationBuilder.CreateIndex(
                name: "IX_CircuitAssignments_CircuitId",
                table: "CircuitAssignments",
                column: "CircuitId",
                unique: true,
                filter: "[ValidUntilUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CircuitAssignments_CircuitId_ValidFromUtc",
                table: "CircuitAssignments",
                columns: new[] { "CircuitId", "ValidFromUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CircuitAssignments_LotId",
                table: "CircuitAssignments",
                column: "LotId");

            migrationBuilder.AddForeignKey(
                name: "FK_CircuitAssignments_Circuits_CircuitId",
                table: "CircuitAssignments",
                column: "CircuitId",
                principalTable: "Circuits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CircuitAssignments_Lots_LotId",
                table: "CircuitAssignments",
                column: "LotId",
                principalTable: "Lots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.RenameColumn(
                name: "NodeLotAssignmentId",
                table: "SensorReadings",
                newName: "CircuitAssignmentId");

            migrationBuilder.RenameIndex(
                name: "IX_SensorReadings_NodeLotAssignmentId",
                table: "SensorReadings",
                newName: "IX_SensorReadings_CircuitAssignmentId");

            migrationBuilder.AddForeignKey(
                name: "FK_SensorReadings_CircuitAssignments_CircuitAssignmentId",
                table: "SensorReadings",
                column: "CircuitAssignmentId",
                principalTable: "CircuitAssignments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // --- Lotes: el circuito de su última asignación ---
            migrationBuilder.AddColumn<int>(
                name: "CircuitId",
                table: "Lots",
                type: "int",
                nullable: true);

            migrationBuilder.Sql(@"
UPDATE l SET CircuitId = (
    SELECT TOP 1 a.CircuitId FROM CircuitAssignments a WHERE a.LotId = l.Id ORDER BY a.ValidFromUtc DESC)
FROM Lots l;");

            migrationBuilder.CreateIndex(
                name: "IX_Lots_CircuitId",
                table: "Lots",
                column: "CircuitId");

            migrationBuilder.AddForeignKey(
                name: "FK_Lots_Circuits_CircuitId",
                table: "Lots",
                column: "CircuitId",
                principalTable: "Circuits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // --- Visitas de Optimización: el circuito del nodo visitado ---
            migrationBuilder.RenameColumn(
                name: "NodeId",
                table: "UserRecentVisits",
                newName: "CircuitId");

            migrationBuilder.Sql(@"
UPDATE v SET CircuitId = c.Id
FROM UserRecentVisits v
JOIN Circuits c ON c.NodeId = v.CircuitId;");

            // --- El nodo ya no tiene tanque ni modo: son del circuito ---
            migrationBuilder.DropColumn(
                name: "ControlMode",
                table: "IotNodes");

            migrationBuilder.DropColumn(
                name: "ReservoirLiters",
                table: "IotNodes");

            migrationBuilder.DropColumn(
                name: "SourceWaterEc",
                table: "IotNodes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Vuelta al modelo por nodo: el nodo toma el control del tanque de su primer circuito y las asignaciones
            // vuelven a ser por nodo. Los circuitos extra de una misma Pi se pierden.
            migrationBuilder.AddColumn<string>(
                name: "ControlMode",
                table: "IotNodes",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "MANUAL");

            migrationBuilder.AddColumn<decimal>(
                name: "ReservoirLiters",
                table: "IotNodes",
                type: "decimal(10,1)",
                nullable: false,
                defaultValue: 500m);

            migrationBuilder.AddColumn<decimal>(
                name: "SourceWaterEc",
                table: "IotNodes",
                type: "decimal(4,2)",
                nullable: false,
                defaultValue: 0.2m);

            migrationBuilder.Sql(@"
UPDATE n SET ControlMode = c.ControlMode, ReservoirLiters = c.ReservoirLiters, SourceWaterEc = c.SourceWaterEc
FROM IotNodes n
JOIN (SELECT *, ROW_NUMBER() OVER (PARTITION BY NodeId ORDER BY Id) AS rn FROM Circuits) c ON c.NodeId = n.Id AND c.rn = 1;");

            migrationBuilder.Sql(@"
UPDATE v SET CircuitId = c.NodeId FROM UserRecentVisits v JOIN Circuits c ON c.Id = v.CircuitId;");
            migrationBuilder.RenameColumn(
                name: "CircuitId",
                table: "UserRecentVisits",
                newName: "NodeId");

            migrationBuilder.DropForeignKey(
                name: "FK_Lots_Circuits_CircuitId",
                table: "Lots");
            migrationBuilder.DropIndex(
                name: "IX_Lots_CircuitId",
                table: "Lots");
            migrationBuilder.DropColumn(
                name: "CircuitId",
                table: "Lots");

            migrationBuilder.DropForeignKey(
                name: "FK_SensorReadings_CircuitAssignments_CircuitAssignmentId",
                table: "SensorReadings");
            migrationBuilder.RenameIndex(
                name: "IX_SensorReadings_CircuitAssignmentId",
                table: "SensorReadings",
                newName: "IX_SensorReadings_NodeLotAssignmentId");
            migrationBuilder.RenameColumn(
                name: "CircuitAssignmentId",
                table: "SensorReadings",
                newName: "NodeLotAssignmentId");

            migrationBuilder.DropForeignKey(
                name: "FK_CircuitAssignments_Circuits_CircuitId",
                table: "CircuitAssignments");
            migrationBuilder.DropForeignKey(
                name: "FK_CircuitAssignments_Lots_LotId",
                table: "CircuitAssignments");
            migrationBuilder.DropIndex(
                name: "IX_CircuitAssignments_CircuitId",
                table: "CircuitAssignments");
            migrationBuilder.DropIndex(
                name: "IX_CircuitAssignments_CircuitId_ValidFromUtc",
                table: "CircuitAssignments");
            migrationBuilder.DropIndex(
                name: "IX_CircuitAssignments_LotId",
                table: "CircuitAssignments");
            migrationBuilder.Sql(@"
UPDATE a SET CircuitId = c.NodeId FROM CircuitAssignments a JOIN Circuits c ON c.Id = a.CircuitId;");
            migrationBuilder.RenameColumn(
                name: "CircuitId",
                table: "CircuitAssignments",
                newName: "NodeId");
            migrationBuilder.Sql("EXEC sp_rename N'dbo.PK_CircuitAssignments', N'PK_NodeLotAssignments', N'OBJECT';");
            migrationBuilder.RenameTable(
                name: "CircuitAssignments",
                newName: "NodeLotAssignments");
            migrationBuilder.CreateIndex(
                name: "IX_NodeLotAssignments_LotId",
                table: "NodeLotAssignments",
                column: "LotId");
            migrationBuilder.CreateIndex(
                name: "IX_NodeLotAssignments_NodeId",
                table: "NodeLotAssignments",
                column: "NodeId",
                unique: true,
                filter: "[ValidUntilUtc] IS NULL");
            migrationBuilder.AddForeignKey(
                name: "FK_NodeLotAssignments_IotNodes_NodeId",
                table: "NodeLotAssignments",
                column: "NodeId",
                principalTable: "IotNodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "FK_NodeLotAssignments_Lots_LotId",
                table: "NodeLotAssignments",
                column: "LotId",
                principalTable: "Lots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "FK_SensorReadings_NodeLotAssignments_NodeLotAssignmentId",
                table: "SensorReadings",
                column: "NodeLotAssignmentId",
                principalTable: "NodeLotAssignments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.DropForeignKey(
                name: "FK_ControlPauses_Circuits_CircuitId",
                table: "ControlPauses");
            migrationBuilder.Sql(@"
UPDATE p SET CircuitId = c.NodeId FROM ControlPauses p JOIN Circuits c ON c.Id = p.CircuitId;");
            migrationBuilder.RenameIndex(
                name: "IX_ControlPauses_CircuitId_Parameter_PausedAtUtc",
                table: "ControlPauses",
                newName: "IX_ControlPauses_NodeId_Parameter_PausedAtUtc");
            migrationBuilder.RenameColumn(
                name: "CircuitId",
                table: "ControlPauses",
                newName: "NodeId");
            migrationBuilder.AddForeignKey(
                name: "FK_ControlPauses_IotNodes_NodeId",
                table: "ControlPauses",
                column: "NodeId",
                principalTable: "IotNodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropForeignKey(
                name: "FK_ActuatorCommands_Circuits_CircuitId",
                table: "ActuatorCommands");
            migrationBuilder.DropIndex(
                name: "IX_ActuatorCommands_CircuitId_CreatedAtUtc",
                table: "ActuatorCommands");
            migrationBuilder.DropColumn(
                name: "CircuitId",
                table: "ActuatorCommands");

            migrationBuilder.DropForeignKey(
                name: "FK_Actuators_Circuits_CircuitId",
                table: "Actuators");
            migrationBuilder.DropIndex(
                name: "IX_Actuators_CircuitId_Kind",
                table: "Actuators");
            migrationBuilder.DropColumn(
                name: "CircuitId",
                table: "Actuators");
            migrationBuilder.CreateIndex(
                name: "IX_Actuators_NodeId_Kind",
                table: "Actuators",
                columns: new[] { "NodeId", "Kind" },
                unique: true);

            migrationBuilder.DropForeignKey(
                name: "FK_Sensors_Circuits_CircuitId",
                table: "Sensors");
            migrationBuilder.DropIndex(
                name: "IX_Sensors_CircuitId",
                table: "Sensors");
            migrationBuilder.DropColumn(
                name: "CircuitId",
                table: "Sensors");

            migrationBuilder.DropTable(
                name: "Circuits");
        }
    }
}
