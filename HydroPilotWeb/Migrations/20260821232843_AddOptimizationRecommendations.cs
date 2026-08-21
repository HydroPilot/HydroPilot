using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddOptimizationRecommendations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CostPriceCatalogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CropTypeId = table.Column<int>(type: "int", nullable: true),
                    LotId = table.Column<int>(type: "int", nullable: true),
                    Destination = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Item = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(14,4)", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostPriceCatalogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CostPriceCatalogs_CropTypes_CropTypeId",
                        column: x => x.CropTypeId,
                        principalTable: "CropTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CostPriceCatalogs_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OptimizationRecommendations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LotId = table.Column<int>(type: "int", nullable: false),
                    RecommendationType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Priority = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CurrentValue = table.Column<decimal>(type: "decimal(12,4)", nullable: true),
                    TargetValue = table.Column<decimal>(type: "decimal(12,4)", nullable: true),
                    TargetLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Explanation = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EstimatedImpact = table.Column<decimal>(type: "decimal(10,4)", nullable: true),
                    ImpactUnit = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ConfidencePercent = table.Column<int>(type: "int", nullable: false),
                    DataSourceSummary = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsMockData = table.Column<bool>(type: "bit", nullable: false),
                    RuleVersion = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    GeneratedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SnapshotHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
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
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PerformedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    ActionedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
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
                    Label = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(12,4)", nullable: true),
                    Target = table.Column<decimal>(type: "decimal(12,4)", nullable: true),
                    Unit = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Order = table.Column<int>(type: "int", nullable: false)
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
                name: "IX_CostPriceCatalogs_CropTypeId",
                table: "CostPriceCatalogs",
                column: "CropTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_CostPriceCatalogs_Destination_Item",
                table: "CostPriceCatalogs",
                columns: new[] { "Destination", "Item" });

            migrationBuilder.CreateIndex(
                name: "IX_CostPriceCatalogs_LotId",
                table: "CostPriceCatalogs",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRecommendations_GeneratedAtUtc",
                table: "OptimizationRecommendations",
                column: "GeneratedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRecommendations_LotId_RecommendationType_SnapshotHash",
                table: "OptimizationRecommendations",
                columns: new[] { "LotId", "RecommendationType", "SnapshotHash" },
                unique: true);

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CostPriceCatalogs");

            migrationBuilder.DropTable(
                name: "RecommendationActions");

            migrationBuilder.DropTable(
                name: "RecommendationDetails");

            migrationBuilder.DropTable(
                name: "OptimizationRecommendations");
        }
    }
}
