using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using HydroPilotWeb.Models;

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

    public DbSet<DailyWeatherForecast> DailyWeatherForecasts => Set<DailyWeatherForecast>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

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
            entity.Property(e => e.PlantedAreaM2).HasColumnType("decimal(10,2)");
            entity.Property(e => e.ActualYieldKg).HasColumnType("decimal(10,2)");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

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
        });

        modelBuilder.Entity<Prediction>(entity =>
        {
            entity.ToTable("Predictions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.AccumulatedGdd).HasColumnType("decimal(8,2)");
            entity.Property(e => e.EstimatedYield).HasColumnType("decimal(8,2)");
            entity.Property(e => e.ModelVersion).HasMaxLength(50);

            entity.HasIndex(e => e.LotId);
            entity.HasIndex(e => e.GeneratedAt);

            entity.HasOne(e => e.Lot)
                  .WithMany(l => l.Predictions)
                  .HasForeignKey(e => e.LotId)
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
    }
}
