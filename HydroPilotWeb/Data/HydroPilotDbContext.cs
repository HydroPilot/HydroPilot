using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Optimization;

namespace HydroPilotWeb.Data;

public class HydroPilotDbContext : DbContext
{
    public HydroPilotDbContext(DbContextOptions<HydroPilotDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<WeatherRecord> WeatherRecords => Set<WeatherRecord>();

    public DbSet<SensorType> SensorTypes => Set<SensorType>();
    public DbSet<MeasurementUnit> MeasurementUnits => Set<MeasurementUnit>();
    public DbSet<Greenhouse> Greenhouses => Set<Greenhouse>();
    public DbSet<IotNode> IotNodes => Set<IotNode>();
    public DbSet<Sensor> Sensors => Set<Sensor>();
    public DbSet<SensorReading> SensorReadings => Set<SensorReading>();
    public DbSet<TelemetryBatch> TelemetryBatches => Set<TelemetryBatch>();
    public DbSet<TelemetryRejection> TelemetryRejections => Set<TelemetryRejection>();
    public DbSet<NodeLotAssignment> NodeLotAssignments => Set<NodeLotAssignment>();

    public DbSet<CropType> CropTypes => Set<CropType>();
    public DbSet<LotStatus> LotStatuses => Set<LotStatus>();
    public DbSet<Lot> Lots => Set<Lot>();
    public DbSet<Prediction> Predictions => Set<Prediction>();

    // --- Dominio de lotes y plantas (plan 09) ---
    public DbSet<PhenologicalStage> PhenologicalStages => Set<PhenologicalStage>();
    public DbSet<CommercialStage> CommercialStages => Set<CommercialStage>();
    public DbSet<BabyLeafConfig> BabyLeafConfigs => Set<BabyLeafConfig>();
    public DbSet<BabyLeafCriterion> BabyLeafCriteria => Set<BabyLeafCriterion>();
    public DbSet<Plant> Plants => Set<Plant>();
    public DbSet<PlantImage> PlantImages => Set<PlantImage>();
    public DbSet<PlantImageAnalysis> PlantImageAnalyses => Set<PlantImageAnalysis>();
    public DbSet<BabyLeafEvaluation> BabyLeafEvaluations => Set<BabyLeafEvaluation>();
    public DbSet<PlantStageHistory> PlantStageHistories => Set<PlantStageHistory>();

    public DbSet<DailyWeatherForecast> DailyWeatherForecasts => Set<DailyWeatherForecast>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    // --- Módulo de optimización (plan 16) ---
    public DbSet<OptimizationRecommendation> OptimizationRecommendations => Set<OptimizationRecommendation>();
    public DbSet<RecommendationDetail> RecommendationDetails => Set<RecommendationDetail>();
    public DbSet<RecommendationAction> RecommendationActions => Set<RecommendationAction>();
    public DbSet<CostPriceCatalog> CostPriceCatalogs => Set<CostPriceCatalog>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.ConfigureWarnings(w =>
            w.Ignore(RelationalEventId.PendingModelChangesWarning));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => e.GoogleSub).IsUnique();
            entity.Property(e => e.GoogleSub).IsRequired().HasMaxLength(256);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(256);
            entity.Property(e => e.GivenName).HasMaxLength(128);
            entity.Property(e => e.Surname).HasMaxLength(128);
            entity.Property(e => e.Role).HasMaxLength(64);
        });

        modelBuilder.Entity<WeatherRecord>(entity =>
        {
            entity.ToTable("WeatherRecords");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Description).HasMaxLength(128);
        });

        modelBuilder.Entity<SensorType>(entity =>
        {
            entity.ToTable("SensorTypes");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => e.Name).IsUnique();
        });

        modelBuilder.Entity<MeasurementUnit>(entity =>
        {
            entity.ToTable("MeasurementUnits");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Symbol).HasMaxLength(10);
            entity.HasIndex(e => e.Name).IsUnique();
        });

        modelBuilder.Entity<Greenhouse>(entity =>
        {
            entity.ToTable("Greenhouses");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(150);
            entity.Property(e => e.Location).HasMaxLength(255);
            entity.Property(e => e.Latitude).HasColumnType("decimal(9,6)");
            entity.Property(e => e.Longitude).HasColumnType("decimal(9,6)");
            entity.Property(e => e.TimeZoneId).HasMaxLength(100);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .IsRequired(false)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<IotNode>(entity =>
        {
            entity.ToTable("IotNodes");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Identifier).IsRequired().HasMaxLength(100);
            entity.Property(e => e.FirmwareVersion).HasMaxLength(50);
            entity.Property(e => e.Status).HasMaxLength(30).HasDefaultValue("ACTIVO");
            entity.Property(e => e.ConnectionState)
                  .HasMaxLength(20)
                  .HasDefaultValue(TelemetryContract.ConnectionNeverConnected);
            entity.Property(e => e.ExpectedIntervalSeconds).HasDefaultValue(300);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

            entity.HasIndex(e => e.Identifier).IsUnique();

            entity.HasOne(e => e.Greenhouse)
                  .WithMany(g => g.IotNodes)
                  .HasForeignKey(e => e.GreenhouseId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Sensor>(entity =>
        {
            entity.ToTable("Sensors");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Model).HasMaxLength(100);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.TechnicalKey).IsRequired().HasMaxLength(100);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

            entity.HasIndex(e => e.Name).IsUnique();
            entity.HasIndex(e => new { e.NodeId, e.TechnicalKey }).IsUnique();

            entity.HasOne(e => e.Node)
                  .WithMany(n => n.Sensors)
                  .HasForeignKey(e => e.NodeId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.SensorType)
                  .WithMany(t => t.Sensors)
                  .HasForeignKey(e => e.SensorTypeId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.MeasurementUnit)
                  .WithMany(u => u.Sensors)
                  .HasForeignKey(e => e.MeasurementUnitId)
                  .IsRequired(false)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SensorReading>(entity =>
        {
            entity.ToTable("SensorReadings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Value).HasColumnType("decimal(12,4)").IsRequired();
            entity.Property(e => e.ExternalReadingId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Quality).IsRequired().HasMaxLength(20).HasDefaultValue(TelemetryContract.QualityValid);
            entity.Property(e => e.QualityReason).HasMaxLength(100);
            entity.Property(e => e.IngestionResult).IsRequired().HasMaxLength(20).HasDefaultValue(TelemetryContract.ResultAceptada);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.SensorId);
            entity.HasIndex(e => e.LotId);
            entity.HasIndex(e => e.NodeId);
            entity.HasIndex(e => e.ObservedAtUtc);
            entity.HasIndex(e => new { e.NodeId, e.ExternalReadingId }).IsUnique();

            entity.HasOne(e => e.Sensor)
                  .WithMany(s => s.Readings)
                  .HasForeignKey(e => e.SensorId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Node)
                  .WithMany()
                  .HasForeignKey(e => e.NodeId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.MeasurementUnit)
                  .WithMany(u => u.Readings)
                  .HasForeignKey(e => e.MeasurementUnitId)
                  .IsRequired(false)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Lot)
                  .WithMany()
                  .HasForeignKey(e => e.LotId)
                  .IsRequired(false)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.LotAssignment)
                  .WithMany()
                  .HasForeignKey(e => e.NodeLotAssignmentId)
                  .IsRequired(false)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<TelemetryBatch>(entity =>
        {
            entity.ToTable("TelemetryBatches");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.BatchId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.SchemaVersion).IsRequired().HasMaxLength(10);
            entity.Property(e => e.FirmwareVersion).HasMaxLength(50);
            entity.Property(e => e.PayloadHash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Result).IsRequired().HasMaxLength(20).HasDefaultValue(TelemetryContract.BatchResultProcesado);
            entity.Property(e => e.ResponseJson);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

            entity.HasIndex(e => new { e.NodeId, e.BatchId }).IsUnique();

            entity.HasOne(e => e.Node)
                  .WithMany(n => n.Batches)
                  .HasForeignKey(e => e.NodeId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TelemetryRejection>(entity =>
        {
            entity.ToTable("TelemetryRejections");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.BatchId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.ReadingId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.SensorRef).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Value).HasColumnType("decimal(12,4)");
            entity.Property(e => e.Unit).HasMaxLength(20);
            entity.Property(e => e.Reason).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Quality).HasMaxLength(20);
            entity.Property(e => e.Result).IsRequired().HasMaxLength(20);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

            entity.HasIndex(e => new { e.NodeId, e.BatchId });

            entity.HasOne(e => e.Node)
                  .WithMany(n => n.Rejections)
                  .HasForeignKey(e => e.NodeId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<NodeLotAssignment>(entity =>
        {
            entity.ToTable("NodeLotAssignments");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Source).IsRequired().HasMaxLength(20).HasDefaultValue(TelemetryContract.AssignmentSourceManual);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

            // Un nodo solo puede tener una asignación abierta a la vez.
            entity.HasIndex(e => e.NodeId)
                  .IsUnique()
                  .HasFilter("[ValidUntilUtc] IS NULL");

            entity.HasOne(e => e.Node)
                  .WithMany(n => n.LotAssignments)
                  .HasForeignKey(e => e.NodeId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Lot)
                  .WithMany()
                  .HasForeignKey(e => e.LotId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CropType>(entity =>
        {
            entity.ToTable("CropTypes");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.GddTarget).HasColumnType("decimal(8,2)");
            entity.Property(e => e.BaseTemperature).HasColumnType("decimal(5,2)");
            entity.Property(e => e.OptimalPhMin).HasColumnType("decimal(4,2)");
            entity.Property(e => e.OptimalPhTarget).HasColumnType("decimal(4,2)");
            entity.Property(e => e.OptimalPhMax).HasColumnType("decimal(4,2)");
            entity.Property(e => e.OptimalEcMin).HasColumnType("decimal(6,2)");
            entity.Property(e => e.OptimalEcMax).HasColumnType("decimal(6,2)");
            entity.Property(e => e.YieldPerM2).HasColumnType("decimal(8,2)");
        });

        modelBuilder.Entity<LotStatus>(entity =>
        {
            entity.ToTable("LotStatuses");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(50);
            entity.HasIndex(e => e.Name).IsUnique();
        });

        modelBuilder.Entity<Lot>(entity =>
        {
            entity.ToTable("Lots");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Name).HasMaxLength(150);
            entity.Property(e => e.PlantedAreaM2).HasColumnType("decimal(10,2)");
            entity.Property(e => e.ActualYieldKg).HasColumnType("decimal(10,2)");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
            entity.Property(e => e.AccumulatedGdd).HasColumnType("decimal(8,2)");
            entity.Property(e => e.CurrentPh).HasColumnType("decimal(4,2)");
            entity.Property(e => e.CurrentEc).HasColumnType("decimal(6,2)");
            entity.Property(e => e.BabyLeafHarvestTargetPercent).HasColumnType("decimal(5,2)");

            entity.HasIndex(e => e.StatusId);
            entity.HasIndex(e => e.CropTypeId);

            entity.HasOne(e => e.Greenhouse)
                  .WithMany()
                  .HasForeignKey(e => e.GreenhouseId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.CropType)
                  .WithMany(c => c.Lots)
                  .HasForeignKey(e => e.CropTypeId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Status)
                  .WithMany(s => s.Lots)
                  .HasForeignKey(e => e.StatusId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.AppliedPhenologicalStage)
                  .WithMany()
                  .HasForeignKey(e => e.AppliedPhenologicalStageId)
                  .IsRequired(false)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.PredominantPhenologicalStage)
                  .WithMany()
                  .HasForeignKey(e => e.PredominantPhenologicalStageId)
                  .IsRequired(false)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.PredominantCommercialStage)
                  .WithMany()
                  .HasForeignKey(e => e.PredominantCommercialStageId)
                  .IsRequired(false)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Prediction>(entity =>
        {
            entity.ToTable("Predictions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.AccumulatedGdd).HasColumnType("decimal(8,2)");
            entity.Property(e => e.EstimatedYield).HasColumnType("decimal(8,2)");
            entity.Property(e => e.ModelVersion).HasMaxLength(50);
            entity.Property(e => e.AsOfDate).HasColumnType("date");
            entity.Property(e => e.DataSource).HasMaxLength(60);
            entity.Property(e => e.CoveragePercent).HasColumnType("decimal(5,2)");

            entity.HasIndex(e => e.LotId);
            entity.HasIndex(e => e.GeneratedAt);
            // Idempotencia (F-05): una consulta repetida por (lote, fecha de cálculo,
            // versión) actualiza la misma fila en vez de duplicar predicciones.
            // Filtrado: las filas legadas con AsOfDate null no participan del índice único.
            entity.HasIndex(e => new { e.LotId, e.AsOfDate, e.ModelVersion })
                  .IsUnique()
                  .HasFilter("[AsOfDate] IS NOT NULL");

            entity.HasOne(e => e.Lot)
                  .WithMany(l => l.Predictions)
                  .HasForeignKey(e => e.LotId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PhenologicalStage>(entity =>
        {
            entity.ToTable("PhenologicalStages");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(300);
            entity.Property(e => e.GddMin).HasColumnType("decimal(8,2)");
            entity.Property(e => e.GddMax).HasColumnType("decimal(8,2)");
            entity.Property(e => e.EcMin).HasColumnType("decimal(6,2)");
            entity.Property(e => e.EcObjective).HasColumnType("decimal(6,2)");
            entity.Property(e => e.EcMax).HasColumnType("decimal(6,2)");

            entity.HasIndex(e => new { e.CropTypeId, e.Order });

            entity.HasOne(e => e.CropType)
                  .WithMany()
                  .HasForeignKey(e => e.CropTypeId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CommercialStage>(entity =>
        {
            entity.ToTable("CommercialStages");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(300);

            entity.HasIndex(e => new { e.CropTypeId, e.Name });

            entity.HasOne(e => e.CropType)
                  .WithMany()
                  .HasForeignKey(e => e.CropTypeId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BabyLeafConfig>(entity =>
        {
            entity.ToTable("BabyLeafConfigs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(300);
            entity.Property(e => e.GddMin).HasColumnType("decimal(8,2)");
            entity.Property(e => e.GddMax).HasColumnType("decimal(8,2)");
            entity.Property(e => e.ScoreMinCandidate).HasColumnType("decimal(5,2)");
            entity.Property(e => e.ScoreMinReady).HasColumnType("decimal(5,2)");
            entity.Property(e => e.Version).HasMaxLength(20);

            entity.HasOne(e => e.CropType)
                  .WithMany()
                  .HasForeignKey(e => e.CropTypeId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BabyLeafCriterion>(entity =>
        {
            entity.ToTable("BabyLeafCriteria");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.DataType).IsRequired().HasMaxLength(30);
            entity.Property(e => e.Unit).HasMaxLength(20);
            entity.Property(e => e.ValueMin).HasColumnType("decimal(10,2)");
            entity.Property(e => e.ValueMax).HasColumnType("decimal(10,2)");
            entity.Property(e => e.Weight).HasColumnType("decimal(5,2)");

            entity.HasOne(e => e.BabyLeafConfig)
                  .WithMany(c => c.Criteria)
                  .HasForeignKey(e => e.BabyLeafConfigId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Plant>(entity =>
        {
            entity.ToTable("Plants");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.OperationalState)
                  .HasConversion<string>()
                  .HasMaxLength(20);
            entity.Property(e => e.DiscardReason).HasMaxLength(300);
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("GETUTCDATE()");

            // (LoteId, Fila, Columna) única: una posición admite una sola planta.
            entity.HasIndex(e => new { e.LotId, e.Row, e.Column }).IsUnique();
            entity.HasIndex(e => e.LotId);
            entity.HasIndex(e => e.CommercialStageId);

            entity.HasOne(e => e.Lot)
                  .WithMany(l => l.Plants)
                  .HasForeignKey(e => e.LotId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.PhenologicalStage)
                  .WithMany()
                  .HasForeignKey(e => e.PhenologicalStageId)
                  .IsRequired(false)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.CommercialStage)
                  .WithMany()
                  .HasForeignKey(e => e.CommercialStageId)
                  .IsRequired(false)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PlantImage>(entity =>
        {
            entity.ToTable("PlantImages");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Path).IsRequired().HasMaxLength(500);
            entity.Property(e => e.FileName).HasMaxLength(255);
            entity.Property(e => e.ModelVersion).HasMaxLength(50);

            entity.HasIndex(e => e.PlantId);
            entity.HasIndex(e => e.CapturedAtUtc);

            entity.HasOne(e => e.Plant)
                  .WithMany(p => p.Images)
                  .HasForeignKey(e => e.PlantId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlantImageAnalysis>(entity =>
        {
            entity.ToTable("PlantImageAnalyses");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.PlantArea).HasColumnType("decimal(12,2)");
            entity.Property(e => e.FoliarArea).HasColumnType("decimal(12,2)");
            entity.Property(e => e.LeafCount).HasColumnType("decimal(8,2)");
            entity.Property(e => e.PlantWidth).HasColumnType("decimal(8,2)");
            entity.Property(e => e.PlantHeight).HasColumnType("decimal(8,2)");
            entity.Property(e => e.RosetteDiameter).HasColumnType("decimal(8,2)");
            entity.Property(e => e.LeafLength).HasColumnType("decimal(8,2)");
            entity.Property(e => e.AverageColor).HasMaxLength(50);
            entity.Property(e => e.GreennessIndex).HasColumnType("decimal(8,4)");
            entity.Property(e => e.DamagePercent).HasColumnType("decimal(8,2)");
            entity.Property(e => e.GrowthRate).HasColumnType("decimal(8,2)");
            entity.Property(e => e.ImageQuality).HasColumnType("decimal(8,4)");
            entity.Property(e => e.ModelVersion).IsRequired().HasMaxLength(50);

            entity.HasOne(e => e.PlantImage)
                  .WithOne(i => i.Analysis)
                  .HasForeignKey<PlantImageAnalysis>(e => e.PlantImageId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BabyLeafEvaluation>(entity =>
        {
            entity.ToTable("BabyLeafEvaluations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.BabyLeafScore).HasColumnType("decimal(5,2)");
            entity.Property(e => e.Result).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Confidence).HasColumnType("decimal(6,4)");
            entity.Property(e => e.ModelVersion).IsRequired().HasMaxLength(50);
            entity.Property(e => e.GddAtEvaluation).HasColumnType("decimal(8,2)");
            entity.Property(e => e.FoliarAreaUsed).HasColumnType("decimal(12,2)");
            entity.Property(e => e.LeafLengthUsed).HasColumnType("decimal(8,2)");
            entity.Property(e => e.GrowthRateUsed).HasColumnType("decimal(8,2)");

            entity.HasIndex(e => e.PlantId);
            entity.HasIndex(e => e.EvaluatedAtUtc);

            entity.HasOne(e => e.Plant)
                  .WithMany(p => p.Evaluations)
                  .HasForeignKey(e => e.PlantId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.BabyLeafConfig)
                  .WithMany()
                  .HasForeignKey(e => e.BabyLeafConfigId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.PlantImage)
                  .WithMany(i => i.Evaluations)
                  .HasForeignKey(e => e.PlantImageId)
                  .IsRequired(false)
                  .OnDelete(DeleteBehavior.Restrict); // Restrict evita múltiples rutas de cascade (Plantas→Evaluaciones y Plantas→Imágenes)
        });

        modelBuilder.Entity<PlantStageHistory>(entity =>
        {
            entity.ToTable("PlantStageHistories");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Source).IsRequired().HasMaxLength(30);
            entity.Property(e => e.Reason).HasMaxLength(300);
            entity.Property(e => e.PreviousOperationalState).HasMaxLength(20);
            entity.Property(e => e.NewOperationalState).HasMaxLength(20);

            entity.HasIndex(e => e.PlantId);
            entity.HasIndex(e => e.ChangedAtUtc);

            entity.HasOne(e => e.Plant)
                  .WithMany(p => p.History)
                  .HasForeignKey(e => e.PlantId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DailyWeatherForecast>(entity =>
        {
            entity.ToTable("DailyWeatherForecasts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.TempMin).HasColumnType("decimal(5,2)");
            entity.Property(e => e.TempMax).HasColumnType("decimal(5,2)");
            entity.HasIndex(e => e.Date).IsUnique();
        });

        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.ToTable("AppSettings");
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Value).IsRequired().HasMaxLength(500);
        });

        // --- Módulo de optimización (plan 16): recomendaciones informativas ---
        modelBuilder.Entity<OptimizationRecommendation>(entity =>
        {
            entity.ToTable("OptimizationRecommendations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.RecommendationType).IsRequired().HasMaxLength(40);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Direction).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Priority).IsRequired().HasMaxLength(20);
            entity.Property(e => e.CurrentValue).HasColumnType("decimal(12,4)");
            entity.Property(e => e.TargetValue).HasColumnType("decimal(12,4)");
            entity.Property(e => e.TargetLabel).HasMaxLength(200);
            entity.Property(e => e.Explanation).IsRequired();
            entity.Property(e => e.EstimatedImpact).HasColumnType("decimal(10,4)");
            entity.Property(e => e.ImpactUnit).HasMaxLength(30);
            entity.Property(e => e.DataSourceSummary).HasMaxLength(200);
            entity.Property(e => e.RuleVersion).IsRequired().HasMaxLength(40);
            entity.Property(e => e.DecisionNote).HasMaxLength(500);
            entity.Property(e => e.SnapshotJson).IsRequired();
            entity.Property(e => e.SnapshotHash).IsRequired().HasMaxLength(64);

            // Idempotencia (OPT-04): por (lote, tipo, snapshot) existe UNA
            // recomendación; repetir el cálculo con el mismo snapshot no duplica.
            entity.HasIndex(e => new { e.LotId, e.RecommendationType, e.SnapshotHash }).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.GeneratedAtUtc);

            entity.HasOne(e => e.Lot)
                  .WithMany()
                  .HasForeignKey(e => e.LotId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RecommendationDetail>(entity =>
        {
            entity.ToTable("RecommendationDetails");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Label).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Kind).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Value).HasColumnType("decimal(12,4)");
            entity.Property(e => e.Target).HasColumnType("decimal(12,4)");
            entity.Property(e => e.Unit).HasMaxLength(30);
            entity.Property(e => e.Note).HasMaxLength(300);

            entity.HasOne(e => e.Recommendation)
                  .WithMany(r => r.Details)
                  .HasForeignKey(e => e.RecommendationId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RecommendationAction>(entity =>
        {
            entity.ToTable("RecommendationActions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Action).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Note).HasMaxLength(500);
            entity.Property(e => e.PerformedBy).HasMaxLength(150);

            entity.HasIndex(e => e.RecommendationId);

            entity.HasOne(e => e.Recommendation)
                  .WithMany(r => r.Actions)
                  .HasForeignKey(e => e.RecommendationId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CostPriceCatalog>(entity =>
        {
            entity.ToTable("CostPriceCatalogs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Destination).IsRequired().HasMaxLength(30);
            entity.Property(e => e.Item).IsRequired().HasMaxLength(30);
            entity.Property(e => e.Value).HasColumnType("decimal(14,4)");
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(10);
            entity.Property(e => e.ValidFrom).HasColumnType("date");
            entity.Property(e => e.ValidUntil).HasColumnType("date");
            entity.Property(e => e.Source).IsRequired().HasMaxLength(30);
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("GETUTCDATE()");

            entity.HasIndex(e => new { e.Destination, e.Item });

            entity.HasOne(e => e.CropType)
                  .WithMany()
                  .HasForeignKey(e => e.CropTypeId)
                  .IsRequired(false)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Lot)
                  .WithMany()
                  .HasForeignKey(e => e.LotId)
                  .IsRequired(false)
                  .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
