using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Models.Optimization;
using HydroPilotWeb.Models.Notifications;

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
    public DbSet<Circuit> Circuits => Set<Circuit>();
    public DbSet<CircuitAssignment> CircuitAssignments => Set<CircuitAssignment>();

    public DbSet<CropType> CropTypes => Set<CropType>();
    public DbSet<LotStatus> LotStatuses => Set<LotStatus>();
    public DbSet<Lot> Lots => Set<Lot>();
    public DbSet<LotComment> LotComments => Set<LotComment>();
    public DbSet<UserRecentVisit> UserRecentVisits => Set<UserRecentVisit>();
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

    // --- Módulo de anomalías (plan 15 / ANO-03) ---
    public DbSet<AnomalyEvent> AnomalyEvents => Set<AnomalyEvent>();
    public DbSet<AnomalyRuleCatalog> AnomalyRuleCatalogs => Set<AnomalyRuleCatalog>();

    // --- Catálogo de precios por destino (Lotes, Simulación y Parámetros) ---
    public DbSet<CostPriceCatalog> CostPriceCatalogs => Set<CostPriceCatalog>();

    // --- Módulo de notificaciones (plan 17) ---
    public DbSet<NotificationAlert> NotificationAlerts => Set<NotificationAlert>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<NotificationAttempt> NotificationAttempts => Set<NotificationAttempt>();

    // --- Control de pH y CE (actuadores) ---
    public DbSet<Actuator> Actuators => Set<Actuator>();
    public DbSet<ActuatorCommand> ActuatorCommands => Set<ActuatorCommand>();
    public DbSet<LotSetpointHistory> LotSetpointHistories => Set<LotSetpointHistory>();
    public DbSet<ControlPause> ControlPauses => Set<ControlPause>();
    public DbSet<SensorFault> SensorFaults => Set<SensorFault>();
    public DbSet<LotRecipe> LotRecipes => Set<LotRecipe>();

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

        modelBuilder.Entity<Circuit>(entity =>
        {
            entity.ToTable("Circuits");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(Circuit.NameMaxLength);
            entity.Property(e => e.ControlMode).IsRequired().HasMaxLength(10).HasDefaultValue(ControlContract.ModeManual);
            entity.Property(e => e.ReservoirLiters).HasColumnType("decimal(10,1)").HasDefaultValue(ControlContract.DefaultReservoirLiters);
            entity.Property(e => e.SourceWaterEc).HasColumnType("decimal(4,2)").HasDefaultValue(ControlContract.DefaultSourceWaterEc);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
            entity.Property(e => e.Kind).IsRequired().HasMaxLength(10).HasDefaultValue(CircuitKinds.Dense);
            entity.Property(e => e.GridRows).HasDefaultValue(Circuit.DefaultGridRows);
            entity.Property(e => e.GridColumns).HasDefaultValue(Circuit.DefaultGridColumns);
            entity.Ignore(e => e.Holes);

            entity.HasIndex(e => new { e.GreenhouseId, e.Name }).IsUnique();

            entity.HasOne(e => e.Greenhouse)
                  .WithMany(g => g.Circuits)
                  .HasForeignKey(e => e.GreenhouseId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Node)
                  .WithMany(n => n.Circuits)
                  .HasForeignKey(e => e.NodeId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Actuator>(entity =>
        {
            entity.ToTable("Actuators");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Kind).IsRequired().HasMaxLength(20);
            entity.Property(e => e.TechnicalKey).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Unit).IsRequired().HasMaxLength(5);
            entity.Property(e => e.MaxPerCommand).HasColumnType("decimal(10,2)");
            entity.Property(e => e.MaxPerDay).HasColumnType("decimal(10,2)");
            entity.Property(e => e.EffectPerUnit).HasColumnType("decimal(10,4)");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

            // Una bomba de cada tipo por circuito; la clave técnica es única en la Pi (maneja varios circuitos).
            entity.HasIndex(e => new { e.CircuitId, e.Kind }).IsUnique();
            entity.HasIndex(e => new { e.NodeId, e.TechnicalKey }).IsUnique();

            entity.HasOne(e => e.Node)
                  .WithMany()
                  .HasForeignKey(e => e.NodeId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Circuit)
                  .WithMany(c => c.Actuators)
                  .HasForeignKey(e => e.CircuitId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ActuatorCommand>(entity =>
        {
            entity.ToTable("ActuatorCommands");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Source).IsRequired().HasMaxLength(10);
            entity.Property(e => e.RequestedBy).HasMaxLength(200);
            entity.Property(e => e.Amount).HasColumnType("decimal(10,2)");
            entity.Property(e => e.Unit).IsRequired().HasMaxLength(5);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
            entity.Property(e => e.ExecutedAmount).HasColumnType("decimal(10,2)");
            entity.Property(e => e.ResultError).HasMaxLength(300);
            entity.Property(e => e.Parameter).IsRequired().HasMaxLength(5);
            entity.Property(e => e.ValueBefore).HasColumnType("decimal(12,4)");
            entity.Property(e => e.BandMin).HasColumnType("decimal(6,2)");
            entity.Property(e => e.Target).HasColumnType("decimal(6,2)");
            entity.Property(e => e.BandMax).HasColumnType("decimal(6,2)");
            entity.Property(e => e.ControllerVersion).IsRequired().HasMaxLength(30);
            entity.Property(e => e.Strength).HasColumnType("decimal(4,2)");
            entity.Property(e => e.ExpectedEffect).HasColumnType("decimal(8,4)");
            entity.Property(e => e.ValueAfter).HasColumnType("decimal(12,4)");
            entity.Property(e => e.ObservedEffect).HasColumnType("decimal(8,4)");

            // Pendientes del nodo (lo que pide la Pi) y últimos comandos (vista Control).
            entity.HasIndex(e => new { e.NodeId, e.Status });
            entity.HasIndex(e => new { e.NodeId, e.CreatedAtUtc });
            entity.HasIndex(e => new { e.CircuitId, e.CreatedAtUtc });
            // Una sola dosis en curso por bomba: dos pedidos simultáneos no pueden duplicar la dosis.
            entity.HasIndex(e => e.ActuatorId)
                  .IsUnique()
                  .HasFilter("[Status] IN ('PENDING', 'DELIVERED')")
                  .HasDatabaseName("IX_ActuatorCommands_ActuatorId_InFlight");

            entity.HasOne(e => e.Node)
                  .WithMany()
                  .HasForeignKey(e => e.NodeId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Circuit)
                  .WithMany()
                  .HasForeignKey(e => e.CircuitId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Actuator)
                  .WithMany()
                  .HasForeignKey(e => e.ActuatorId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Lot)
                  .WithMany()
                  .HasForeignKey(e => e.LotId)
                  .IsRequired(false)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<LotRecipe>(entity =>
        {
            entity.ToTable("LotRecipes");
            entity.HasKey(e => e.LotId);
            entity.Property(e => e.LotId).ValueGeneratedNever();
            entity.Property(e => e.HistoryPhOffset).HasColumnType("decimal(4,2)");
            entity.Property(e => e.HistoryEcOffset).HasColumnType("decimal(4,2)");
            entity.Property(e => e.PhOffset).HasColumnType("decimal(4,2)");
            entity.Property(e => e.EcOffset).HasColumnType("decimal(4,2)");
            entity.Property(e => e.Trust).HasColumnType("decimal(3,2)");
            entity.Property(e => e.Evidence).HasColumnType("decimal(6,2)");
            entity.Property(e => e.ScoreTrend).HasColumnType("decimal(6,2)");
            entity.Property(e => e.Reason).HasMaxLength(400);

            entity.HasOne(e => e.Lot)
                  .WithOne()
                  .HasForeignKey<LotRecipe>(e => e.LotId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SensorFault>(entity =>
        {
            entity.ToTable("SensorFaults");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.SensorType).IsRequired().HasMaxLength(30);
            entity.Property(e => e.Kind).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Value).HasColumnType("decimal(12,4)");
            entity.Property(e => e.PreviousValue).HasColumnType("decimal(12,4)");
            entity.Property(e => e.Detail).HasMaxLength(200);
            entity.Property(e => e.ResolvedBy).HasMaxLength(200);

            entity.HasIndex(e => new { e.NodeId, e.ResolvedAtUtc });
            entity.HasIndex(e => new { e.SensorId, e.Kind, e.DetectedAtUtc });
            // Un solo error abierto por sensor y tipo.
            entity.HasIndex(e => new { e.SensorId, e.Kind })
                  .IsUnique()
                  .HasFilter("[ResolvedAtUtc] IS NULL")
                  .HasDatabaseName("IX_SensorFaults_Open");

            entity.HasOne(e => e.Node)
                  .WithMany()
                  .HasForeignKey(e => e.NodeId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Sensor)
                  .WithMany()
                  .HasForeignKey(e => e.SensorId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ControlPause>(entity =>
        {
            entity.ToTable("ControlPauses");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Parameter).IsRequired().HasMaxLength(5);
            entity.Property(e => e.Reason).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ResumedBy).HasMaxLength(200);

            entity.HasIndex(e => new { e.CircuitId, e.Parameter, e.PausedAtUtc });
            // Una sola pausa abierta por magnitud y circuito.
            entity.HasIndex(e => new { e.CircuitId, e.Parameter })
                  .IsUnique()
                  .HasFilter("[ResumedAtUtc] IS NULL")
                  .HasDatabaseName("IX_ControlPauses_Open");

            entity.HasOne(e => e.Circuit)
                  .WithMany()
                  .HasForeignKey(e => e.CircuitId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LotSetpointHistory>(entity =>
        {
            entity.ToTable("LotSetpointHistories");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.PhMin).HasColumnType("decimal(6,2)");
            entity.Property(e => e.PhTarget).HasColumnType("decimal(6,2)");
            entity.Property(e => e.PhMax).HasColumnType("decimal(6,2)");
            entity.Property(e => e.EcMin).HasColumnType("decimal(6,2)");
            entity.Property(e => e.EcTarget).HasColumnType("decimal(6,2)");
            entity.Property(e => e.EcMax).HasColumnType("decimal(6,2)");
            entity.Property(e => e.PhOffset).HasColumnType("decimal(4,2)");
            entity.Property(e => e.EcOffset).HasColumnType("decimal(4,2)");
            entity.Property(e => e.Source).IsRequired().HasMaxLength(20);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

            entity.HasIndex(e => new { e.LotId, e.ValidFromUtc });

            entity.HasOne(e => e.Lot)
                  .WithMany()
                  .HasForeignKey(e => e.LotId)
                  .OnDelete(DeleteBehavior.Cascade);
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

            // pH y CE son del circuito; temperatura y humedad, del invernadero (sin circuito).
            entity.HasOne(e => e.Circuit)
                  .WithMany(c => c.Sensors)
                  .HasForeignKey(e => e.CircuitId)
                  .IsRequired(false)
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
            // Última lectura de un lote (pH/CE de la vista Lotes) y lecturas de un sensor en un período (GDD).
            entity.HasIndex(e => new { e.LotId, e.ObservedAtUtc });
            entity.HasIndex(e => new { e.SensorId, e.ObservedAtUtc });
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

            entity.HasOne(e => e.Assignment)
                  .WithMany()
                  .HasForeignKey(e => e.CircuitAssignmentId)
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

        modelBuilder.Entity<CircuitAssignment>(entity =>
        {
            entity.ToTable("CircuitAssignments");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Source).IsRequired().HasMaxLength(20).HasDefaultValue(TelemetryContract.AssignmentSourceManual);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

            // Un circuito tiene un solo lote en curso.
            entity.HasIndex(e => e.CircuitId)
                  .IsUnique()
                  .HasFilter("[ValidUntilUtc] IS NULL");
            entity.HasIndex(e => new { e.CircuitId, e.ValidFromUtc });

            entity.HasOne(e => e.Circuit)
                  .WithMany(c => c.Assignments)
                  .HasForeignKey(e => e.CircuitId)
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
            entity.Property(e => e.BabyLeafGoalPercent).HasColumnType("decimal(5,2)");
            entity.Property(e => e.ExpectedActivePercent).HasColumnType("decimal(5,2)");
            entity.Property(e => e.HarvestBabyLeafGramsPerPlant).HasColumnType("decimal(7,1)");
            entity.Property(e => e.HarvestConventionalGramsPerPlant).HasColumnType("decimal(7,1)");
            entity.Property(e => e.HarvestBabyLeafPricePerKg).HasColumnType("decimal(12,2)");
            entity.Property(e => e.HarvestConventionalPricePerKg).HasColumnType("decimal(12,2)");
            entity.Property(e => e.HarvestPriceCurrency).HasMaxLength(10);
            entity.Property(e => e.CycleAverageDailyGdd).HasColumnType("decimal(6,2)");
            entity.Property(e => e.BabyLeafPeakGdd).HasColumnType("decimal(8,2)");
            entity.Property(e => e.TransplantCostPerPlant).HasColumnType("decimal(12,2)");

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

            entity.HasOne(e => e.Circuit)
                  .WithMany()
                  .HasForeignKey(e => e.CircuitId)
                  .IsRequired(false)
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

        modelBuilder.Entity<LotComment>(entity =>
        {
            entity.ToTable("LotComments");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.AuthorName).IsRequired().HasMaxLength(256);
            entity.Property(e => e.Text).IsRequired().HasMaxLength(LotComment.TextMaxLength);
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("GETUTCDATE()");

            // La lista del modal: comentarios del lote, más nuevos arriba.
            entity.HasIndex(e => new { e.LotId, e.CreatedAtUtc });

            // Se borran con el lote; si se borra el usuario, el comentario queda con su nombre guardado.
            entity.HasOne(e => e.Lot)
                  .WithMany()
                  .HasForeignKey(e => e.LotId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .IsRequired(false)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<UserRecentVisit>(entity =>
        {
            entity.ToTable("UserRecentVisits");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Kind).IsRequired().HasMaxLength(30);

            // "Últimos accedidos" del usuario, más recientes primero.
            entity.HasIndex(e => new { e.UserId, e.VisitedAtUtc });

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Lot)
                  .WithMany()
                  .HasForeignKey(e => e.LotId)
                  .IsRequired(false)
                  .OnDelete(DeleteBehavior.Cascade);
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

            entity.Ignore(e => e.IsAdultRoute);

            // (Lote, Circuito, Fila, Columna) única: un hueco del circuito admite una sola planta del lote.
            // Sin filtro: las plantas sin circuito (lotes de antes de los circuitos) también tienen posición única.
            entity.HasIndex(e => new { e.LotId, e.CircuitId, e.Row, e.Column }).IsUnique().HasFilter(null);
            entity.HasIndex(e => e.LotId);
            entity.HasIndex(e => e.CircuitId);

            entity.HasOne(e => e.Circuit)
                  .WithMany()
                  .HasForeignKey(e => e.CircuitId)
                  .IsRequired(false)
                  .OnDelete(DeleteBehavior.Restrict);
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
            entity.Property(e => e.SizeRatio).HasColumnType("decimal(6,3)");

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

        modelBuilder.Entity<AnomalyEvent>(entity =>
        {
            entity.ToTable("AnomalyEvents");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Type).IsRequired().HasMaxLength(60);
            entity.Property(e => e.Severity).IsRequired().HasMaxLength(20);
            entity.Property(e => e.ObservedValue).HasColumnType("decimal(12,4)");
            entity.Property(e => e.PeakValue).HasColumnType("decimal(12,4)");
            entity.Property(e => e.TargetValue).HasColumnType("decimal(12,4)");
            entity.Property(e => e.OperationalMin).HasColumnType("decimal(12,4)");
            entity.Property(e => e.OperationalMax).HasColumnType("decimal(12,4)");
            entity.Property(e => e.RuleCode).IsRequired().HasMaxLength(60);
            entity.Property(e => e.RuleDescription).HasMaxLength(500);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Fingerprint).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Origin).IsRequired().HasMaxLength(30);
            entity.Property(e => e.ResolutionReason).HasMaxLength(200);
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("GETUTCDATE()");

            // Deduplicación global por corrida: un mismo episodio jamás se duplica,
            // incluso ante reinicios o barreras concurrentes del worker.
            entity.HasIndex(e => e.Fingerprint).IsUnique();
            entity.HasIndex(e => e.LotId);
            entity.HasIndex(e => e.SensorId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.Type);
            entity.HasIndex(e => e.FirstObservedAtUtc);
            entity.HasIndex(e => e.LastObservedAtUtc);

            entity.HasOne(e => e.Lot)
                  .WithMany()
                  .HasForeignKey(e => e.LotId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AnomalyRuleCatalog>(entity =>
        {
            entity.ToTable("AnomalyRuleCatalogs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Code).IsRequired().HasMaxLength(60);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.SensorTypeName).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Source).IsRequired().HasMaxLength(30);
            entity.Property(e => e.Notes).HasMaxLength(300);
            entity.Property(e => e.PhysicalMin).HasColumnType("decimal(12,4)");
            entity.Property(e => e.PhysicalMax).HasColumnType("decimal(12,4)");
            entity.Property(e => e.OperationalMin).HasColumnType("decimal(12,4)");
            entity.Property(e => e.OperationalMax).HasColumnType("decimal(12,4)");
            entity.Property(e => e.TargetValue).HasColumnType("decimal(12,4)");

            entity.HasIndex(e => new { e.CropTypeId, e.Code }).IsUnique();

            entity.HasOne(e => e.CropType)
                  .WithMany()
                  .HasForeignKey(e => e.CropTypeId)
                  .OnDelete(DeleteBehavior.Restrict);
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

        // --- Módulo de notificaciones (plan 17) ---
        modelBuilder.Entity<NotificationAlert>(entity =>
        {
            entity.ToTable("NotificationAlerts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Type).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Severity).IsRequired().HasMaxLength(30);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Message).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.Fingerprint).IsRequired().HasMaxLength(100);
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("GETUTCDATE()");

            entity.HasIndex(e => e.Fingerprint).IsUnique().HasFilter("[Fingerprint] <> ''");
            entity.HasIndex(e => new { e.CreatedAtUtc, e.Type, e.Severity });

            entity.HasOne(e => e.Lot)
                  .WithMany()
                  .HasForeignKey(e => e.LotId)
                  .IsRequired(false)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<NotificationPreference>(entity =>
        {
            entity.ToTable("NotificationPreferences");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.MinSeverityEmail).IsRequired().HasMaxLength(30);
            entity.Property(e => e.UpdatedAtUtc).HasDefaultValueSql("GETUTCDATE()");

            entity.HasIndex(e => e.UserId).IsUnique();

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NotificationDelivery>(entity =>
        {
            entity.ToTable("NotificationDeliveries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Channel).IsRequired().HasMaxLength(30);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(30);
            entity.Property(e => e.Recipient).IsRequired().HasMaxLength(256);
            entity.Property(e => e.LastError).HasMaxLength(1000);
            entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("GETUTCDATE()");

            entity.HasIndex(e => new { e.Status, e.Channel });
            entity.HasIndex(e => new { e.UserId, e.IsRead });

            entity.HasOne(e => e.Alert)
                  .WithMany(a => a.Deliveries)
                  .HasForeignKey(e => e.AlertId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NotificationAttempt>(entity =>
        {
            entity.ToTable("NotificationAttempts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.ErrorMessage).HasMaxLength(1000);
            entity.Property(e => e.AttemptedAtUtc).HasDefaultValueSql("GETUTCDATE()");

            entity.HasIndex(e => e.DeliveryId);

            entity.HasOne(e => e.Delivery)
                  .WithMany(d => d.AttemptsList)
                  .HasForeignKey(e => e.DeliveryId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
