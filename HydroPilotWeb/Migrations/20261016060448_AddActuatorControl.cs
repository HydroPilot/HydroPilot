using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddActuatorControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ControlMode",
                table: "IotNodes",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "MANUAL");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastCommandPollAtUtc",
                table: "IotNodes",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ReservoirLiters",
                table: "IotNodes",
                type: "decimal(10,1)",
                nullable: false,
                defaultValue: 500m);

            migrationBuilder.CreateTable(
                name: "Actuators",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NodeId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TechnicalKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    MaxPerCommand = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    MaxPerDay = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Actuators", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Actuators_IotNodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "IotNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LotSetpointHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LotId = table.Column<int>(type: "int", nullable: false),
                    ValidFromUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PhMin = table.Column<decimal>(type: "decimal(6,2)", nullable: true),
                    PhTarget = table.Column<decimal>(type: "decimal(6,2)", nullable: true),
                    PhMax = table.Column<decimal>(type: "decimal(6,2)", nullable: true),
                    EcMin = table.Column<decimal>(type: "decimal(6,2)", nullable: true),
                    EcTarget = table.Column<decimal>(type: "decimal(6,2)", nullable: true),
                    EcMax = table.Column<decimal>(type: "decimal(6,2)", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StageId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LotSetpointHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LotSetpointHistories_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ActuatorCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeId = table.Column<int>(type: "int", nullable: false),
                    ActuatorId = table.Column<int>(type: "int", nullable: false),
                    LotId = table.Column<int>(type: "int", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    RequestedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DeliveredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExecutedAmount = table.Column<decimal>(type: "decimal(10,2)", nullable: true),
                    ResultError = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    DeviceStartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeviceFinishedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Parameter = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    TriggerReadingId = table.Column<int>(type: "int", nullable: true),
                    ValueBefore = table.Column<decimal>(type: "decimal(12,4)", nullable: true),
                    BandMin = table.Column<decimal>(type: "decimal(6,2)", nullable: true),
                    Target = table.Column<decimal>(type: "decimal(6,2)", nullable: true),
                    BandMax = table.Column<decimal>(type: "decimal(6,2)", nullable: true),
                    StageId = table.Column<int>(type: "int", nullable: true),
                    ControllerVersion = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActuatorCommands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActuatorCommands_Actuators_ActuatorId",
                        column: x => x.ActuatorId,
                        principalTable: "Actuators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ActuatorCommands_IotNodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "IotNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ActuatorCommands_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActuatorCommands_ActuatorId_InFlight",
                table: "ActuatorCommands",
                column: "ActuatorId",
                unique: true,
                filter: "[Status] IN ('PENDING', 'DELIVERED')");

            migrationBuilder.CreateIndex(
                name: "IX_ActuatorCommands_LotId",
                table: "ActuatorCommands",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_ActuatorCommands_NodeId_CreatedAtUtc",
                table: "ActuatorCommands",
                columns: new[] { "NodeId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ActuatorCommands_NodeId_Status",
                table: "ActuatorCommands",
                columns: new[] { "NodeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Actuators_NodeId_Kind",
                table: "Actuators",
                columns: new[] { "NodeId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Actuators_NodeId_TechnicalKey",
                table: "Actuators",
                columns: new[] { "NodeId", "TechnicalKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LotSetpointHistories_LotId_ValidFromUtc",
                table: "LotSetpointHistories",
                columns: new[] { "LotId", "ValidFromUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActuatorCommands");

            migrationBuilder.DropTable(
                name: "LotSetpointHistories");

            migrationBuilder.DropTable(
                name: "Actuators");

            migrationBuilder.DropColumn(
                name: "ControlMode",
                table: "IotNodes");

            migrationBuilder.DropColumn(
                name: "LastCommandPollAtUtc",
                table: "IotNodes");

            migrationBuilder.DropColumn(
                name: "ReservoirLiters",
                table: "IotNodes");
        }
    }
}
