using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddRecommendationLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OptimizationRecommendations_LotId_RecommendationType_SnapshotHash",
                table: "OptimizationRecommendations");

            migrationBuilder.AlterColumn<decimal>(
                name: "EstimatedImpact",
                table: "OptimizationRecommendations",
                type: "decimal(14,2)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(10,4)",
                oldNullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedAtUtc",
                table: "OptimizationRecommendations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DueDate",
                table: "OptimizationRecommendations",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fingerprint",
                table: "OptimizationRecommendations",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastEvaluatedAtUtc",
                table: "OptimizationRecommendations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RealizedAmount",
                table: "OptimizationRecommendations",
                type: "decimal(14,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "OptimizationRecommendations",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRecommendations_LotId_Fingerprint",
                table: "OptimizationRecommendations",
                columns: new[] { "LotId", "Fingerprint" });

            // Las recomendaciones de la lógica anterior quedan en el historial: las abiertas pasan a "ya no
            // aplica" y todas se marcan cerradas (la lógica nueva solo abre las suyas, con huella).
            migrationBuilder.Sql(@"
UPDATE OptimizationRecommendations
SET Status = 'EXPIRED',
    DecisionNote = N'Reemplazada por la lógica nueva de Optimización.'
WHERE Status IN ('PENDING', 'BLOCKED');

UPDATE OptimizationRecommendations
SET ClosedAtUtc = COALESCE(DecidedAtUtc, GeneratedAtUtc)
WHERE ClosedAtUtc IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OptimizationRecommendations_LotId_Fingerprint",
                table: "OptimizationRecommendations");

            migrationBuilder.DropColumn(
                name: "ClosedAtUtc",
                table: "OptimizationRecommendations");

            migrationBuilder.DropColumn(
                name: "DueDate",
                table: "OptimizationRecommendations");

            migrationBuilder.DropColumn(
                name: "Fingerprint",
                table: "OptimizationRecommendations");

            migrationBuilder.DropColumn(
                name: "LastEvaluatedAtUtc",
                table: "OptimizationRecommendations");

            migrationBuilder.DropColumn(
                name: "RealizedAmount",
                table: "OptimizationRecommendations");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "OptimizationRecommendations");

            migrationBuilder.AlterColumn<decimal>(
                name: "EstimatedImpact",
                table: "OptimizationRecommendations",
                type: "decimal(10,4)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(14,2)",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRecommendations_LotId_RecommendationType_SnapshotHash",
                table: "OptimizationRecommendations",
                columns: new[] { "LotId", "RecommendationType", "SnapshotHash" },
                unique: true);
        }
    }
}
