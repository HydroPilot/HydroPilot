using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Eliminar filas viejas de Sensors (esquema anterior incompatible)
            // DbInitializer regenera los datos con el nuevo esquema
            migrationBuilder.Sql("DELETE FROM [Sensors]");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "Sensors");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                table: "Sensors",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastCalibrationDate",
                table: "Sensors",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MeasurementUnitId",
                table: "Sensors",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "Sensors",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NodeId",
                table: "Sensors",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SensorTypeId",
                table: "Sensors",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Greenhouses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Latitude = table.Column<decimal>(type: "decimal(9,6)", nullable: true),
                    Longitude = table.Column<decimal>(type: "decimal(9,6)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Greenhouses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Greenhouses_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MeasurementUnits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeasurementUnits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SensorTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SensorTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IotNodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GreenhouseId = table.Column<int>(type: "int", nullable: false),
                    Identifier = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FirmwareVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false, defaultValue: "ACTIVO"),
                    LastConnection = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IotNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IotNodes_Greenhouses_GreenhouseId",
                        column: x => x.GreenhouseId,
                        principalTable: "Greenhouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SensorReadings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SensorId = table.Column<int>(type: "int", nullable: false),
                    LotId = table.Column<int>(type: "int", nullable: true),
                    Value = table.Column<decimal>(type: "decimal(12,4)", nullable: false),
                    MeasurementUnitId = table.Column<int>(type: "int", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SensorReadings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SensorReadings_MeasurementUnits_MeasurementUnitId",
                        column: x => x.MeasurementUnitId,
                        principalTable: "MeasurementUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SensorReadings_Sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "Sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sensors_MeasurementUnitId",
                table: "Sensors",
                column: "MeasurementUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_Sensors_Name",
                table: "Sensors",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sensors_NodeId",
                table: "Sensors",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_Sensors_SensorTypeId",
                table: "Sensors",
                column: "SensorTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Greenhouses_UserId",
                table: "Greenhouses",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_IotNodes_GreenhouseId",
                table: "IotNodes",
                column: "GreenhouseId");

            migrationBuilder.CreateIndex(
                name: "IX_IotNodes_Identifier",
                table: "IotNodes",
                column: "Identifier",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeasurementUnits_Name",
                table: "MeasurementUnits",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SensorReadings_MeasurementUnitId",
                table: "SensorReadings",
                column: "MeasurementUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_SensorReadings_SensorId",
                table: "SensorReadings",
                column: "SensorId");

            migrationBuilder.CreateIndex(
                name: "IX_SensorReadings_Timestamp",
                table: "SensorReadings",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_SensorTypes_Name",
                table: "SensorTypes",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Sensors_IotNodes_NodeId",
                table: "Sensors",
                column: "NodeId",
                principalTable: "IotNodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Sensors_MeasurementUnits_MeasurementUnitId",
                table: "Sensors",
                column: "MeasurementUnitId",
                principalTable: "MeasurementUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Sensors_SensorTypes_SensorTypeId",
                table: "Sensors",
                column: "SensorTypeId",
                principalTable: "SensorTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Sensors_IotNodes_NodeId",
                table: "Sensors");

            migrationBuilder.DropForeignKey(
                name: "FK_Sensors_MeasurementUnits_MeasurementUnitId",
                table: "Sensors");

            migrationBuilder.DropForeignKey(
                name: "FK_Sensors_SensorTypes_SensorTypeId",
                table: "Sensors");

            migrationBuilder.DropTable(
                name: "IotNodes");

            migrationBuilder.DropTable(
                name: "SensorReadings");

            migrationBuilder.DropTable(
                name: "SensorTypes");

            migrationBuilder.DropTable(
                name: "Greenhouses");

            migrationBuilder.DropTable(
                name: "MeasurementUnits");

            migrationBuilder.DropIndex(
                name: "IX_Sensors_MeasurementUnitId",
                table: "Sensors");

            migrationBuilder.DropIndex(
                name: "IX_Sensors_Name",
                table: "Sensors");

            migrationBuilder.DropIndex(
                name: "IX_Sensors_NodeId",
                table: "Sensors");

            migrationBuilder.DropIndex(
                name: "IX_Sensors_SensorTypeId",
                table: "Sensors");

            migrationBuilder.DropColumn(
                name: "LastCalibrationDate",
                table: "Sensors");

            migrationBuilder.DropColumn(
                name: "MeasurementUnitId",
                table: "Sensors");

            migrationBuilder.DropColumn(
                name: "Model",
                table: "Sensors");

            migrationBuilder.DropColumn(
                name: "NodeId",
                table: "Sensors");

            migrationBuilder.DropColumn(
                name: "SensorTypeId",
                table: "Sensors");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                table: "Sensors",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValueSql: "GETUTCDATE()");

            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "Sensors",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");
        }
    }
}
