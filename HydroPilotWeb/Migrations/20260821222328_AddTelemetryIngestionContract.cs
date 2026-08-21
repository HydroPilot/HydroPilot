using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddTelemetryIngestionContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sensors_NodeId",
                table: "Sensors");

            migrationBuilder.AddColumn<string>(
                name: "TechnicalKey",
                table: "Sensors",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ExternalReadingId",
                table: "SensorReadings",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "IngestionResult",
                table: "SensorReadings",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "aceptada");

            migrationBuilder.AddColumn<int>(
                name: "NodeId",
                table: "SensorReadings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "NodeLotAssignmentId",
                table: "SensorReadings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ObservedAtUtc",
                table: "SensorReadings",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "Quality",
                table: "SensorReadings",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "VALID");

            migrationBuilder.AddColumn<string>(
                name: "QualityReason",
                table: "SensorReadings",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReceivedAtUtc",
                table: "SensorReadings",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // --- Backfill aditivo de filas existentes (no elimina ni transforma datos) ---
            // Técnica: los sensores preexistentes toman su nombre como clave técnica;
            // las lecturas existentes reciben un ExternalReadingId estable derivado del Id,
            // su nodo de origen desde el sensor, y sus fechas de observación/recepción
            // desde los campos que ya existían (Timestamp/CreatedAt). Los nodos con
            // contacto previo quedan ONLINE; el monitor recalcula después con su timing.
            migrationBuilder.Sql("""
                UPDATE [Sensors]
                SET [TechnicalKey] = [Name]
                WHERE [TechnicalKey] = '';

                UPDATE r
                SET
                    r.[NodeId] = s.[NodeId],
                    r.[ExternalReadingId] = 'legacy-' + CAST(r.[Id] AS nvarchar(20)),
                    r.[ObservedAtUtc] = r.[Timestamp],
                    r.[ReceivedAtUtc] = r.[CreatedAt]
                FROM [SensorReadings] r
                INNER JOIN [Sensors] s ON s.[Id] = r.[SensorId]
                WHERE r.[NodeId] = 0 OR r.[ExternalReadingId] = '' OR r.[ObservedAtUtc] = '0001-01-01T00:00:00';
                """);

            migrationBuilder.AddColumn<string>(
                name: "ConnectionState",
                table: "IotNodes",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NEVER_CONNECTED");

            migrationBuilder.AddColumn<int>(
                name: "ExpectedIntervalSeconds",
                table: "IotNodes",
                type: "int",
                nullable: false,
                defaultValue: 300);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastAcceptedAt",
                table: "IotNodes",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastRejectedAt",
                table: "IotNodes",
                type: "datetime2",
                nullable: true);

            // Backfill de nodos existentes (las columnas ya existen en este punto):
            // con contacto previo quedan ONLINE con LastAcceptedAt = la última conexión
            // conocida; sin contacto, NEVER_CONNECTED. El monitor recalcula con su timing.
            migrationBuilder.Sql("""
                UPDATE [IotNodes]
                SET [LastAcceptedAt] = [LastConnection],
                    [ConnectionState] = 'ONLINE'
                WHERE [LastConnection] IS NOT NULL;

                UPDATE [IotNodes]
                SET [ConnectionState] = 'NEVER_CONNECTED'
                WHERE [LastConnection] IS NULL;
                """);

            migrationBuilder.CreateTable(
                name: "NodeLotAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NodeId = table.Column<int>(type: "int", nullable: false),
                    LotId = table.Column<int>(type: "int", nullable: false),
                    ValidFromUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ValidUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "manual"),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeLotAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NodeLotAssignments_IotNodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "IotNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NodeLotAssignments_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TelemetryBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NodeId = table.Column<int>(type: "int", nullable: false),
                    BatchId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SchemaVersion = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: true),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FirmwareVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Result = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "procesado"),
                    ResponseJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetryBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TelemetryBatches_IotNodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "IotNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TelemetryRejections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NodeId = table.Column<int>(type: "int", nullable: false),
                    BatchId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReadingId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SensorRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(12,4)", nullable: true),
                    Unit = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Quality = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Result = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetryRejections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TelemetryRejections_IotNodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "IotNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sensors_NodeId_TechnicalKey",
                table: "Sensors",
                columns: new[] { "NodeId", "TechnicalKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SensorReadings_NodeId",
                table: "SensorReadings",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_SensorReadings_NodeId_ExternalReadingId",
                table: "SensorReadings",
                columns: new[] { "NodeId", "ExternalReadingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SensorReadings_NodeLotAssignmentId",
                table: "SensorReadings",
                column: "NodeLotAssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_SensorReadings_ObservedAtUtc",
                table: "SensorReadings",
                column: "ObservedAtUtc");

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

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryBatches_NodeId_BatchId",
                table: "TelemetryBatches",
                columns: new[] { "NodeId", "BatchId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryRejections_NodeId_BatchId",
                table: "TelemetryRejections",
                columns: new[] { "NodeId", "BatchId" });

            migrationBuilder.AddForeignKey(
                name: "FK_SensorReadings_IotNodes_NodeId",
                table: "SensorReadings",
                column: "NodeId",
                principalTable: "IotNodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SensorReadings_NodeLotAssignments_NodeLotAssignmentId",
                table: "SensorReadings",
                column: "NodeLotAssignmentId",
                principalTable: "NodeLotAssignments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SensorReadings_IotNodes_NodeId",
                table: "SensorReadings");

            migrationBuilder.DropForeignKey(
                name: "FK_SensorReadings_NodeLotAssignments_NodeLotAssignmentId",
                table: "SensorReadings");

            migrationBuilder.DropTable(
                name: "NodeLotAssignments");

            migrationBuilder.DropTable(
                name: "TelemetryBatches");

            migrationBuilder.DropTable(
                name: "TelemetryRejections");

            migrationBuilder.DropIndex(
                name: "IX_Sensors_NodeId_TechnicalKey",
                table: "Sensors");

            migrationBuilder.DropIndex(
                name: "IX_SensorReadings_NodeId",
                table: "SensorReadings");

            migrationBuilder.DropIndex(
                name: "IX_SensorReadings_NodeId_ExternalReadingId",
                table: "SensorReadings");

            migrationBuilder.DropIndex(
                name: "IX_SensorReadings_NodeLotAssignmentId",
                table: "SensorReadings");

            migrationBuilder.DropIndex(
                name: "IX_SensorReadings_ObservedAtUtc",
                table: "SensorReadings");

            migrationBuilder.DropColumn(
                name: "TechnicalKey",
                table: "Sensors");

            migrationBuilder.DropColumn(
                name: "ExternalReadingId",
                table: "SensorReadings");

            migrationBuilder.DropColumn(
                name: "IngestionResult",
                table: "SensorReadings");

            migrationBuilder.DropColumn(
                name: "NodeId",
                table: "SensorReadings");

            migrationBuilder.DropColumn(
                name: "NodeLotAssignmentId",
                table: "SensorReadings");

            migrationBuilder.DropColumn(
                name: "ObservedAtUtc",
                table: "SensorReadings");

            migrationBuilder.DropColumn(
                name: "Quality",
                table: "SensorReadings");

            migrationBuilder.DropColumn(
                name: "QualityReason",
                table: "SensorReadings");

            migrationBuilder.DropColumn(
                name: "ReceivedAtUtc",
                table: "SensorReadings");

            migrationBuilder.DropColumn(
                name: "ConnectionState",
                table: "IotNodes");

            migrationBuilder.DropColumn(
                name: "ExpectedIntervalSeconds",
                table: "IotNodes");

            migrationBuilder.DropColumn(
                name: "LastAcceptedAt",
                table: "IotNodes");

            migrationBuilder.DropColumn(
                name: "LastRejectedAt",
                table: "IotNodes");

            migrationBuilder.CreateIndex(
                name: "IX_Sensors_NodeId",
                table: "Sensors",
                column: "NodeId");
        }
    }
}
