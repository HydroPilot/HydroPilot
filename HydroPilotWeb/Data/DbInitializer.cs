using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Models;

namespace HydroPilotWeb.Data;

public static class DbInitializer
{
    public static void Initialize(HydroPilotDbContext context)
    {
        context.Database.EnsureCreated();

        if (context.Sensors.Any())
        {
            return;
        }

        context.Sensors.AddRange(
            new Sensor { Name = "ph0", Type = "pH", IsActive = true },
            new Sensor { Name = "temp0", Type = "Temperatura", IsActive = true },
            new Sensor { Name = "hum0", Type = "Humedad", IsActive = false }
        );

        context.SaveChanges();
    }
}
