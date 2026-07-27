using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using HydroPilotWeb.Models;

namespace HydroPilotWeb.Data;

public class HydroPilotDbContext : DbContext
{
    public HydroPilotDbContext(DbContextOptions<HydroPilotDbContext> options) : base(options) { }

    public DbSet<Sensor> Sensors => Set<Sensor>();
    public DbSet<User> Users => Set<User>();
    public DbSet<WeatherRecord> WeatherRecords => Set<WeatherRecord>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.ConfigureWarnings(w =>
            w.Ignore(RelationalEventId.PendingModelChangesWarning));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Sensor>(entity =>
        {
            entity.ToTable("Sensors");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Type).HasMaxLength(50);
        });

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
            entity.Property(e => e.Temp);
            entity.Property(e => e.FeelsLike);
            entity.Property(e => e.Pressure);
            entity.Property(e => e.WindSpeed);
            entity.Property(e => e.Clouds);
            entity.Property(e => e.Visibility);
        });
    }
}
