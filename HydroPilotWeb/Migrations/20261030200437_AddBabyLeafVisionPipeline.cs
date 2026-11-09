using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddBabyLeafVisionPipeline : Migration
    {
        /// <inheritdoc />
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'BabyLeafScore')
    ALTER TABLE [PlantImageAnalyses] ADD [BabyLeafScore] decimal(5,2) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'BabyLeafStatus')
    ALTER TABLE [PlantImageAnalyses] ADD [BabyLeafStatus] nvarchar(50) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'CanopySolidity')
    ALTER TABLE [PlantImageAnalyses] ADD [CanopySolidity] decimal(8,4) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'Confidence')
    ALTER TABLE [PlantImageAnalyses] ADD [Confidence] decimal(6,4) NULL;
ELSE
    ALTER TABLE [PlantImageAnalyses] ALTER COLUMN [Confidence] decimal(6,4) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'HealthClass')
    ALTER TABLE [PlantImageAnalyses] ADD [HealthClass] nvarchar(50) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'HealthConfidence')
    ALTER TABLE [PlantImageAnalyses] ADD [HealthConfidence] decimal(6,4) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'HealthProbabilitiesJson')
    ALTER TABLE [PlantImageAnalyses] ADD [HealthProbabilitiesJson] nvarchar(max) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'LaplacianVariance')
    ALTER TABLE [PlantImageAnalyses] ADD [LaplacianVariance] decimal(10,2) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'PredominantClass')
    ALTER TABLE [PlantImageAnalyses] ADD [PredominantClass] nvarchar(50) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'ProbabilitiesJson')
    ALTER TABLE [PlantImageAnalyses] ADD [ProbabilitiesJson] nvarchar(max) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'SizeRatio')
    ALTER TABLE [PlantImageAnalyses] ADD [SizeRatio] decimal(8,4) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'VegetationCoverageRatio')
    ALTER TABLE [PlantImageAnalyses] ADD [VegetationCoverageRatio] decimal(8,4) NULL;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'BabyLeafScore')
    ALTER TABLE [PlantImageAnalyses] DROP COLUMN [BabyLeafScore];

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'BabyLeafStatus')
    ALTER TABLE [PlantImageAnalyses] DROP COLUMN [BabyLeafStatus];

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'CanopySolidity')
    ALTER TABLE [PlantImageAnalyses] DROP COLUMN [CanopySolidity];

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'Confidence')
    ALTER TABLE [PlantImageAnalyses] DROP COLUMN [Confidence];

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'HealthClass')
    ALTER TABLE [PlantImageAnalyses] DROP COLUMN [HealthClass];

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'HealthConfidence')
    ALTER TABLE [PlantImageAnalyses] DROP COLUMN [HealthConfidence];

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'HealthProbabilitiesJson')
    ALTER TABLE [PlantImageAnalyses] DROP COLUMN [HealthProbabilitiesJson];

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'LaplacianVariance')
    ALTER TABLE [PlantImageAnalyses] DROP COLUMN [LaplacianVariance];

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'PredominantClass')
    ALTER TABLE [PlantImageAnalyses] DROP COLUMN [PredominantClass];

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'ProbabilitiesJson')
    ALTER TABLE [PlantImageAnalyses] DROP COLUMN [ProbabilitiesJson];

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'SizeRatio')
    ALTER TABLE [PlantImageAnalyses] DROP COLUMN [SizeRatio];

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlantImageAnalyses') AND name = 'VegetationCoverageRatio')
    ALTER TABLE [PlantImageAnalyses] DROP COLUMN [VegetationCoverageRatio];
");
        }
    }
}
