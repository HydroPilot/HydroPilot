using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Models;

namespace HydroPilotWeb.Data;

public static class DbInitializer
{
    public static void Initialize(HydroPilotDbContext context, IConfiguration configuration)
    {
        context.Database.Migrate();

        if (!context.Sensors.Any())
        {
            context.Sensors.AddRange(
                new Sensor { Name = "ph0", Type = "pH", IsActive = true },
                new Sensor { Name = "temp0", Type = "Temperatura", IsActive = true },
                new Sensor { Name = "hum0", Type = "Humedad", IsActive = false }
            );
        }

        var adminPassword = configuration["Admin:Password"];
        if (!string.IsNullOrWhiteSpace(adminPassword) && !context.Users.Any(u => u.PasswordHash != null))
        {
            context.Users.Add(new User
            {
                GoogleSub = "admin",
                Email = "admin@hydropilot.local",
                GivenName = "Admin",
                Surname = "",
                Role = "Administrador",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(adminPassword),
                CreatedAt = DateTime.UtcNow,
                LastLoginAt = DateTime.UtcNow
            });
        }

        context.SaveChanges();
    }
}
