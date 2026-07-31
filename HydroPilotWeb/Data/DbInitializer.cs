using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Models;

namespace HydroPilotWeb.Data;

public static class DbInitializer
{
    public static void Initialize(HydroPilotDbContext context, IConfiguration configuration)
    {
        context.Database.Migrate();

        // --- Seed de catálogos de telemetría ---
        if (!context.SensorTypes.Any())
        {
            context.SensorTypes.AddRange(
                new SensorType { Name = "pH" },
                new SensorType { Name = "CE" },
                new SensorType { Name = "Temperatura" },
                new SensorType { Name = "Humedad" }
            );
        }

        if (!context.MeasurementUnits.Any())
        {
            context.MeasurementUnits.AddRange(
                new MeasurementUnit { Name = "pH", Symbol = "pH" },
                new MeasurementUnit { Name = "milisiemens por centímetro", Symbol = "mS/cm" },
                new MeasurementUnit { Name = "grados Celsius", Symbol = "°C" },
                new MeasurementUnit { Name = "porcentaje", Symbol = "%" }
            );
        }

        context.SaveChanges();

        // --- Seed de infraestructura demo ---
        if (!context.Greenhouses.Any())
        {
            var admin = context.Users.FirstOrDefault(u => u.Role == "Administrador");
            context.Greenhouses.Add(new Greenhouse
            {
                UserId = admin?.Id,
                Name = "Invernadero Principal",
                Location = "UTN FRBA - Campus",
                CreatedAt = DateTime.UtcNow
            });
            context.SaveChanges();
        }

        var greenhouse = context.Greenhouses.First();

        if (!context.IotNodes.Any())
        {
            context.IotNodes.Add(new IotNode
            {
                GreenhouseId = greenhouse.Id,
                Identifier = "rpi-inv-01",
                Status = "ACTIVO",
                CreatedAt = DateTime.UtcNow
            });
            context.SaveChanges();
        }

        var node = context.IotNodes.First();

        if (!context.Sensors.Any())
        {
            var sensorTypes = context.SensorTypes.ToDictionary(t => t.Name);
            var units = context.MeasurementUnits.ToDictionary(u => u.Name);

            context.Sensors.AddRange(
                new Sensor
                {
                    NodeId = node.Id,
                    SensorTypeId = sensorTypes["pH"].Id,
                    MeasurementUnitId = units["pH"].Id,
                    Name = "ph-solucion",
                    Model = "PH-4502C",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                },
                new Sensor
                {
                    NodeId = node.Id,
                    SensorTypeId = sensorTypes["CE"].Id,
                    MeasurementUnitId = units["milisiemens por centímetro"].Id,
                    Name = "ec-solucion",
                    Model = "TDS-EC-Meter",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                },
                new Sensor
                {
                    NodeId = node.Id,
                    SensorTypeId = sensorTypes["Temperatura"].Id,
                    MeasurementUnitId = units["grados Celsius"].Id,
                    Name = "temp-ambiente",
                    Model = "DHT22",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                },
                new Sensor
                {
                    NodeId = node.Id,
                    SensorTypeId = sensorTypes["Humedad"].Id,
                    MeasurementUnitId = units["porcentaje"].Id,
                    Name = "hum-ambiente",
                    Model = "DHT22",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                }
            );
            context.SaveChanges();
        }

        // --- Admin user (existente) ---
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
