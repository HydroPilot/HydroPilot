using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddTelemetryContractAndLotesDomain : Migration
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

            migrationBuilder.AddColumn<decimal>(
                name: "AccumulatedGdd",
                table: "Lots",
                type: "decimal(8,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AppliedPhenologicalStageId",
                table: "Lots",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BabyLeafHarvestTargetPercent",
                table: "Lots",
                type: "decimal(5,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CurrentEc",
                table: "Lots",
                type: "decimal(6,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CurrentPh",
                table: "Lots",
                type: "decimal(4,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GridColumns",
                table: "Lots",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GridRows",
                table: "Lots",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCommercialStageMixed",
                table: "Lots",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Lots",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PredominantCommercialStageId",
                table: "Lots",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PredominantPhenologicalStageId",
                table: "Lots",
                type: "int",
                nullable: true);

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

            migrationBuilder.AddColumn<decimal>(
                name: "OptimalPhTarget",
                table: "CropTypes",
                type: "decimal(4,2)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BabyLeafConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CropTypeId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    GddMin = table.Column<decimal>(type: "decimal(8,2)", nullable: false),
                    GddMax = table.Column<decimal>(type: "decimal(8,2)", nullable: false),
                    ScoreMinCandidate = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    ScoreMinReady = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BabyLeafConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BabyLeafConfigs_CropTypes_CropTypeId",
                        column: x => x.CropTypeId,
                        principalTable: "CropTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommercialStages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CropTypeId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommercialStages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommercialStages_CropTypes_CropTypeId",
                        column: x => x.CropTypeId,
                        principalTable: "CropTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

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
                name: "PhenologicalStages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CropTypeId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Order = table.Column<int>(type: "int", nullable: false),
                    GddMin = table.Column<decimal>(type: "decimal(8,2)", nullable: false),
                    GddMax = table.Column<decimal>(type: "decimal(8,2)", nullable: false),
                    EcMin = table.Column<decimal>(type: "decimal(6,2)", nullable: false),
                    EcObjective = table.Column<decimal>(type: "decimal(6,2)", nullable: false),
                    EcMax = table.Column<decimal>(type: "decimal(6,2)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhenologicalStages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhenologicalStages_CropTypes_CropTypeId",
                        column: x => x.CropTypeId,
                        principalTable: "CropTypes",
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

            migrationBuilder.CreateTable(
                name: "BabyLeafCriteria",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BabyLeafConfigId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DataType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ValueMin = table.Column<decimal>(type: "decimal(10,2)", nullable: true),
                    ValueMax = table.Column<decimal>(type: "decimal(10,2)", nullable: true),
                    Weight = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    IsMandatory = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BabyLeafCriteria", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BabyLeafCriteria_BabyLeafConfigs_BabyLeafConfigId",
                        column: x => x.BabyLeafConfigId,
                        principalTable: "BabyLeafConfigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Plants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LotId = table.Column<int>(type: "int", nullable: false),
                    Row = table.Column<int>(type: "int", nullable: false),
                    Column = table.Column<int>(type: "int", nullable: false),
                    PhenologicalStageId = table.Column<int>(type: "int", nullable: true),
                    CommercialStageId = table.Column<int>(type: "int", nullable: true),
                    OperationalState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    HarvestDate = table.Column<DateOnly>(type: "date", nullable: true),
                    DiscardDate = table.Column<DateOnly>(type: "date", nullable: true),
                    DiscardReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Plants_CommercialStages_CommercialStageId",
                        column: x => x.CommercialStageId,
                        principalTable: "CommercialStages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Plants_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Plants_PhenologicalStages_PhenologicalStageId",
                        column: x => x.PhenologicalStageId,
                        principalTable: "PhenologicalStages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlantImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PlantId = table.Column<int>(type: "int", nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Width = table.Column<int>(type: "int", nullable: false),
                    Height = table.Column<int>(type: "int", nullable: false),
                    Processed = table.Column<bool>(type: "bit", nullable: false),
                    ModelVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlantImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlantImages_Plants_PlantId",
                        column: x => x.PlantId,
                        principalTable: "Plants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlantStageHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PlantId = table.Column<int>(type: "int", nullable: false),
                    PreviousPhenologicalStageId = table.Column<int>(type: "int", nullable: true),
                    PreviousCommercialStageId = table.Column<int>(type: "int", nullable: true),
                    PreviousOperationalState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    NewPhenologicalStageId = table.Column<int>(type: "int", nullable: true),
                    NewCommercialStageId = table.Column<int>(type: "int", nullable: true),
                    NewOperationalState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ChangedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlantStageHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlantStageHistories_Plants_PlantId",
                        column: x => x.PlantId,
                        principalTable: "Plants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BabyLeafEvaluations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PlantId = table.Column<int>(type: "int", nullable: false),
                    BabyLeafConfigId = table.Column<int>(type: "int", nullable: false),
                    PlantImageId = table.Column<int>(type: "int", nullable: true),
                    EvaluatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    BabyLeafScore = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    Result = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Confidence = table.Column<decimal>(type: "decimal(6,4)", nullable: false),
                    ModelVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    GddAtEvaluation = table.Column<decimal>(type: "decimal(8,2)", nullable: false),
                    MandatoryCriteriaMet = table.Column<bool>(type: "bit", nullable: false),
                    FoliarAreaUsed = table.Column<decimal>(type: "decimal(12,2)", nullable: true),
                    LeafLengthUsed = table.Column<decimal>(type: "decimal(8,2)", nullable: true),
                    GrowthRateUsed = table.Column<decimal>(type: "decimal(8,2)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BabyLeafEvaluations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BabyLeafEvaluations_BabyLeafConfigs_BabyLeafConfigId",
                        column: x => x.BabyLeafConfigId,
                        principalTable: "BabyLeafConfigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BabyLeafEvaluations_PlantImages_PlantImageId",
                        column: x => x.PlantImageId,
                        principalTable: "PlantImages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BabyLeafEvaluations_Plants_PlantId",
                        column: x => x.PlantId,
                        principalTable: "Plants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlantImageAnalyses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PlantImageId = table.Column<int>(type: "int", nullable: false),
                    PlantArea = table.Column<decimal>(type: "decimal(12,2)", nullable: true),
                    FoliarArea = table.Column<decimal>(type: "decimal(12,2)", nullable: true),
                    LeafCount = table.Column<decimal>(type: "decimal(8,2)", nullable: true),
                    PlantWidth = table.Column<decimal>(type: "decimal(8,2)", nullable: true),
                    PlantHeight = table.Column<decimal>(type: "decimal(8,2)", nullable: true),
                    RosetteDiameter = table.Column<decimal>(type: "decimal(8,2)", nullable: true),
                    LeafLength = table.Column<decimal>(type: "decimal(8,2)", nullable: true),
                    AverageColor = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    GreennessIndex = table.Column<decimal>(type: "decimal(8,4)", nullable: true),
                    DamagePercent = table.Column<decimal>(type: "decimal(8,2)", nullable: true),
                    GrowthRate = table.Column<decimal>(type: "decimal(8,2)", nullable: true),
                    ImageQuality = table.Column<decimal>(type: "decimal(8,4)", nullable: true),
                    ModelVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    AnalyzedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlantImageAnalyses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlantImageAnalyses_PlantImages_PlantImageId",
                        column: x => x.PlantImageId,
                        principalTable: "PlantImages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                name: "IX_Lots_AppliedPhenologicalStageId",
                table: "Lots",
                column: "AppliedPhenologicalStageId");

            migrationBuilder.CreateIndex(
                name: "IX_Lots_PredominantCommercialStageId",
                table: "Lots",
                column: "PredominantCommercialStageId");

            migrationBuilder.CreateIndex(
                name: "IX_Lots_PredominantPhenologicalStageId",
                table: "Lots",
                column: "PredominantPhenologicalStageId");

            migrationBuilder.CreateIndex(
                name: "IX_BabyLeafConfigs_CropTypeId",
                table: "BabyLeafConfigs",
                column: "CropTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_BabyLeafCriteria_BabyLeafConfigId",
                table: "BabyLeafCriteria",
                column: "BabyLeafConfigId");

            migrationBuilder.CreateIndex(
                name: "IX_BabyLeafEvaluations_BabyLeafConfigId",
                table: "BabyLeafEvaluations",
                column: "BabyLeafConfigId");

            migrationBuilder.CreateIndex(
                name: "IX_BabyLeafEvaluations_EvaluatedAtUtc",
                table: "BabyLeafEvaluations",
                column: "EvaluatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BabyLeafEvaluations_PlantId",
                table: "BabyLeafEvaluations",
                column: "PlantId");

            migrationBuilder.CreateIndex(
                name: "IX_BabyLeafEvaluations_PlantImageId",
                table: "BabyLeafEvaluations",
                column: "PlantImageId");

            migrationBuilder.CreateIndex(
                name: "IX_CommercialStages_CropTypeId_Name",
                table: "CommercialStages",
                columns: new[] { "CropTypeId", "Name" });

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
                name: "IX_PhenologicalStages_CropTypeId_Order",
                table: "PhenologicalStages",
                columns: new[] { "CropTypeId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_PlantImageAnalyses_PlantImageId",
                table: "PlantImageAnalyses",
                column: "PlantImageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlantImages_CapturedAtUtc",
                table: "PlantImages",
                column: "CapturedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PlantImages_PlantId",
                table: "PlantImages",
                column: "PlantId");

            migrationBuilder.CreateIndex(
                name: "IX_Plants_CommercialStageId",
                table: "Plants",
                column: "CommercialStageId");

            migrationBuilder.CreateIndex(
                name: "IX_Plants_LotId",
                table: "Plants",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_Plants_LotId_Row_Column",
                table: "Plants",
                columns: new[] { "LotId", "Row", "Column" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Plants_PhenologicalStageId",
                table: "Plants",
                column: "PhenologicalStageId");

            migrationBuilder.CreateIndex(
                name: "IX_PlantStageHistories_ChangedAtUtc",
                table: "PlantStageHistories",
                column: "ChangedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PlantStageHistories_PlantId",
                table: "PlantStageHistories",
                column: "PlantId");

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
                name: "FK_Lots_CommercialStages_PredominantCommercialStageId",
                table: "Lots",
                column: "PredominantCommercialStageId",
                principalTable: "CommercialStages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Lots_PhenologicalStages_AppliedPhenologicalStageId",
                table: "Lots",
                column: "AppliedPhenologicalStageId",
                principalTable: "PhenologicalStages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Lots_PhenologicalStages_PredominantPhenologicalStageId",
                table: "Lots",
                column: "PredominantPhenologicalStageId",
                principalTable: "PhenologicalStages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

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
                name: "FK_Lots_CommercialStages_PredominantCommercialStageId",
                table: "Lots");

            migrationBuilder.DropForeignKey(
                name: "FK_Lots_PhenologicalStages_AppliedPhenologicalStageId",
                table: "Lots");

            migrationBuilder.DropForeignKey(
                name: "FK_Lots_PhenologicalStages_PredominantPhenologicalStageId",
                table: "Lots");

            migrationBuilder.DropForeignKey(
                name: "FK_SensorReadings_IotNodes_NodeId",
                table: "SensorReadings");

            migrationBuilder.DropForeignKey(
                name: "FK_SensorReadings_NodeLotAssignments_NodeLotAssignmentId",
                table: "SensorReadings");

            migrationBuilder.DropTable(
                name: "BabyLeafCriteria");

            migrationBuilder.DropTable(
                name: "BabyLeafEvaluations");

            migrationBuilder.DropTable(
                name: "NodeLotAssignments");

            migrationBuilder.DropTable(
                name: "PlantImageAnalyses");

            migrationBuilder.DropTable(
                name: "PlantStageHistories");

            migrationBuilder.DropTable(
                name: "TelemetryBatches");

            migrationBuilder.DropTable(
                name: "TelemetryRejections");

            migrationBuilder.DropTable(
                name: "BabyLeafConfigs");

            migrationBuilder.DropTable(
                name: "PlantImages");

            migrationBuilder.DropTable(
                name: "Plants");

            migrationBuilder.DropTable(
                name: "CommercialStages");

            migrationBuilder.DropTable(
                name: "PhenologicalStages");

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

            migrationBuilder.DropIndex(
                name: "IX_Lots_AppliedPhenologicalStageId",
                table: "Lots");

            migrationBuilder.DropIndex(
                name: "IX_Lots_PredominantCommercialStageId",
                table: "Lots");

            migrationBuilder.DropIndex(
                name: "IX_Lots_PredominantPhenologicalStageId",
                table: "Lots");

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
                name: "AccumulatedGdd",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "AppliedPhenologicalStageId",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "BabyLeafHarvestTargetPercent",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "CurrentEc",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "CurrentPh",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "GridColumns",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "GridRows",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "IsCommercialStageMixed",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "PredominantCommercialStageId",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "PredominantPhenologicalStageId",
                table: "Lots");

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

            migrationBuilder.DropColumn(
                name: "OptimalPhTarget",
                table: "CropTypes");

            migrationBuilder.CreateIndex(
                name: "IX_Sensors_NodeId",
                table: "Sensors",
                column: "NodeId");
        }
    }
}
