using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Models;

namespace HydroPilotWeb.Data;

public class HydroPilotDbContext : DbContext
{
    public HydroPilotDbContext(DbContextOptions<HydroPilotDbContext> options) : base(options) { }

    public DbSet<Sensor> Sensors => Set<Sensor>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Sensor>(entity =>
        {
            entity.ToTable("Sensors");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Type).HasMaxLength(50);

            if (Database.IsSqlServer())
            {
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
            }
            else if (Database.IsSqlite())
            {
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            }
        });
    }
}
