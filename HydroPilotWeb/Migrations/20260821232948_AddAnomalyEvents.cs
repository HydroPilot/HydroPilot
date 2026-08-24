using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddAnomalyEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnomalyEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Type = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    LotId = table.Column<int>(type: "int", nullable: false),
                    GreenhouseId = table.Column<int>(type: "int", nullable: true),
                    NodeId = table.Column<int>(type: "int", nullable: true),
                    SensorId = table.Column<int>(type: "int", nullable: true),
                    Severity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ObservedValue = table.Column<decimal>(type: "decimal(12,4)", nullable: false),
                    TargetValue = table.Column<decimal>(type: "decimal(12,4)", nullable: true),
                    OperationalMin = table.Column<decimal>(type: "decimal(12,4)", nullable: true),
                    OperationalMax = table.Column<decimal>(type: "decimal(12,4)", nullable: true),
                    RuleCode = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    RuleDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    FirstObservedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastObservedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReadingCount = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Origin = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolutionReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnomalyEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnomalyEvents_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AnomalyRuleCatalogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CropTypeId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SensorTypeName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ConsecutiveToOpen = table.Column<int>(type: "int", nullable: false),
                    CooldownMinutes = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    PhysicalMin = table.Column<decimal>(type: "decimal(12,4)", nullable: true),
                    PhysicalMax = table.Column<decimal>(type: "decimal(12,4)", nullable: true),
                    OperationalMin = table.Column<decimal>(type: "decimal(12,4)", nullable: true),
                    OperationalMax = table.Column<decimal>(type: "decimal(12,4)", nullable: true),
                    TargetValue = table.Column<decimal>(type: "decimal(12,4)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnomalyRuleCatalogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnomalyRuleCatalogs_CropTypes_CropTypeId",
                        column: x => x.CropTypeId,
                        principalTable: "CropTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyEvents_Fingerprint",
                table: "AnomalyEvents",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyEvents_FirstObservedAtUtc",
                table: "AnomalyEvents",
                column: "FirstObservedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyEvents_LastObservedAtUtc",
                table: "AnomalyEvents",
                column: "LastObservedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyEvents_LotId",
                table: "AnomalyEvents",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyEvents_SensorId",
                table: "AnomalyEvents",
                column: "SensorId");

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyEvents_Status",
                table: "AnomalyEvents",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyEvents_Type",
                table: "AnomalyEvents",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_AnomalyRuleCatalogs_CropTypeId_Code",
                table: "AnomalyRuleCatalogs",
                columns: new[] { "CropTypeId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnomalyEvents");

            migrationBuilder.DropTable(
                name: "AnomalyRuleCatalogs");
        }
    }
}
