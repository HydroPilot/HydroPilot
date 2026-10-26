using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class RemoveOptimizationRecommendations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Se borró el módulo de recomendaciones: sus avisos, los mínimos de cambio de estrategia y sus tablas.
            migrationBuilder.Sql(@"
DELETE a FROM NotificationAttempts a
    INNER JOIN NotificationDeliveries d ON d.Id = a.DeliveryId
    INNER JOIN NotificationAlerts n ON n.Id = d.AlertId
WHERE n.Type = 'RecomendacionEstrategica';
DELETE d FROM NotificationDeliveries d
    INNER JOIN NotificationAlerts n ON n.Id = d.AlertId
WHERE n.Type = 'RecomendacionEstrategica';
DELETE FROM NotificationAlerts WHERE Type = 'RecomendacionEstrategica';
DELETE FROM AppSettings WHERE [Key] IN ('StrategyMinGainAmount', 'StrategyMinGainPercent');");

            migrationBuilder.DropTable(
                name: "RecommendationActions");

            migrationBuilder.DropTable(
                name: "RecommendationDetails");

            migrationBuilder.DropTable(
                name: "OptimizationRecommendations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OptimizationRecommendations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LotId = table.Column<int>(type: "int", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConfidencePercent = table.Column<int>(type: "int", nullable: false),
                    CurrentValue = table.Column<decimal>(type: "decimal(12,4)", nullable: true),
                    DataSourceSummary = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Direction = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EstimatedImpact = table.Column<decimal>(type: "decimal(14,2)", nullable: true),
                    Explanation = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    GeneratedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ImpactUnit = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    IsMockData = table.Column<bool>(type: "bit", nullable: false),
                    LastEvaluatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Priority = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RealizedAmount = table.Column<decimal>(type: "decimal(14,2)", nullable: true),
                    RecommendationType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    RuleVersion = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SnapshotHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TargetLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TargetValue = table.Column<decimal>(type: "decimal(12,4)", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptimizationRecommendations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OptimizationRecommendations_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecommendationActions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecommendationId = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ActionedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PerformedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecommendationActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecommendationActions_OptimizationRecommendations_RecommendationId",
                        column: x => x.RecommendationId,
                        principalTable: "OptimizationRecommendations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecommendationDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecommendationId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Target = table.Column<decimal>(type: "decimal(12,4)", nullable: true),
                    Unit = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Value = table.Column<decimal>(type: "decimal(12,4)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecommendationDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecommendationDetails_OptimizationRecommendations_RecommendationId",
                        column: x => x.RecommendationId,
                        principalTable: "OptimizationRecommendations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRecommendations_GeneratedAtUtc",
                table: "OptimizationRecommendations",
                column: "GeneratedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRecommendations_LotId_Fingerprint",
                table: "OptimizationRecommendations",
                columns: new[] { "LotId", "Fingerprint" });

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRecommendations_Status",
                table: "OptimizationRecommendations",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationActions_RecommendationId",
                table: "RecommendationActions",
                column: "RecommendationId");

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationDetails_RecommendationId",
                table: "RecommendationDetails",
                column: "RecommendationId");
        }
    }
}
