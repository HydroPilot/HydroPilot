using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddBabyLeafVisionPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "BabyLeafScore",
                table: "PlantImageAnalyses",
                type: "decimal(5,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BabyLeafStatus",
                table: "PlantImageAnalyses",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CanopySolidity",
                table: "PlantImageAnalyses",
                type: "decimal(8,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Confidence",
                table: "PlantImageAnalyses",
                type: "decimal(6,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HealthClass",
                table: "PlantImageAnalyses",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HealthConfidence",
                table: "PlantImageAnalyses",
                type: "decimal(6,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HealthProbabilitiesJson",
                table: "PlantImageAnalyses",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LaplacianVariance",
                table: "PlantImageAnalyses",
                type: "decimal(10,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PredominantClass",
                table: "PlantImageAnalyses",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProbabilitiesJson",
                table: "PlantImageAnalyses",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SizeRatio",
                table: "PlantImageAnalyses",
                type: "decimal(8,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VegetationCoverageRatio",
                table: "PlantImageAnalyses",
                type: "decimal(8,4)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BabyLeafScore",
                table: "PlantImageAnalyses");

            migrationBuilder.DropColumn(
                name: "BabyLeafStatus",
                table: "PlantImageAnalyses");

            migrationBuilder.DropColumn(
                name: "CanopySolidity",
                table: "PlantImageAnalyses");

            migrationBuilder.DropColumn(
                name: "Confidence",
                table: "PlantImageAnalyses");

            migrationBuilder.DropColumn(
                name: "HealthClass",
                table: "PlantImageAnalyses");

            migrationBuilder.DropColumn(
                name: "HealthConfidence",
                table: "PlantImageAnalyses");

            migrationBuilder.DropColumn(
                name: "HealthProbabilitiesJson",
                table: "PlantImageAnalyses");

            migrationBuilder.DropColumn(
                name: "LaplacianVariance",
                table: "PlantImageAnalyses");

            migrationBuilder.DropColumn(
                name: "PredominantClass",
                table: "PlantImageAnalyses");

            migrationBuilder.DropColumn(
                name: "ProbabilitiesJson",
                table: "PlantImageAnalyses");

            migrationBuilder.DropColumn(
                name: "SizeRatio",
                table: "PlantImageAnalyses");

            migrationBuilder.DropColumn(
                name: "VegetationCoverageRatio",
                table: "PlantImageAnalyses");
        }
    }
}
