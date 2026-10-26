using Microsoft.EntityFrameworkCore;
using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Circuits;
using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Data;

public static class DbInitializer
{
    public static void Initialize(HydroPilotDbContext context, IConfiguration configuration)
    {
        // Migraciones que mueven datos (UnifyGreenhousePi pasa las lecturas de una Pi a otra) superan los 30 s por defecto.
        context.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
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
                TimeZoneId = "America/Argentina/Buenos_Aires",
                CreatedAt = DateTime.UtcNow
            });
            context.SaveChanges();
        }

        var greenhouse = context.Greenhouses.First();
        if (string.IsNullOrEmpty(greenhouse.TimeZoneId))
        {
            greenhouse.TimeZoneId = "America/Argentina/Buenos_Aires";
            context.SaveChanges();
        }

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

        var node = context.IotNodes.OrderBy(n => n.Id).First();

        // --- Circuitos: la Pi del invernadero maneja todos (dos para la demo), cada uno con su pH, CE y 4 bombas;
        // temperatura y humedad son del invernadero ---
        if (!context.Circuits.Any())
        {
            // Densos con la grilla del lote demo (6 × 10); los de Adultas los agrega el entorno de simulación o Ajustes > Circuitos.
            CircuitSetup.CreateAsync(context, greenhouse.Id, node.Id, "Circuito 1", ControlContract.DefaultReservoirLiters, default,
                    CircuitKinds.Dense, 6, 10)
                .GetAwaiter().GetResult();
            CircuitSetup.CreateAsync(context, greenhouse.Id, node.Id, "Circuito 2", ControlContract.DefaultReservoirLiters, default,
                    CircuitKinds.Dense, 6, 10)
                .GetAwaiter().GetResult();
        }
        CircuitSetup.EnsureAmbientSensorsAsync(context, greenhouse.Id, node.Id, default).GetAwaiter().GetResult();

        // --- Lote en el circuito (fixture demo, IOT-05/IOT-10) ---
        // El lote se resuelve por fecha de observación. La grilla/posiciones de plantas
        // pertenecen al módulo de lotes y plantas; aquí solo se fija la asignación.
        if (!context.CircuitAssignments.Any() && context.Lots.Any())
        {
            var activeLot = context.Lots.OrderBy(l => l.Id).First();
            var circuit = context.Circuits.OrderBy(c => c.Id).First();
            context.CircuitAssignments.Add(new CircuitAssignment
            {
                CircuitId = circuit.Id,
                LotId = activeLot.Id,
                ValidFromUtc = DateTime.UtcNow.AddDays(-60),
                ValidUntilUtc = null,
                Source = TelemetryContract.AssignmentSourceFixture,
                CreatedAt = DateTime.UtcNow
            });
            activeLot.CircuitId ??= circuit.Id;
            context.SaveChanges();
        }

        // --- Bombas de cada circuito (control de pH y CE) y sondas de los circuitos que no tienen ---
        SeedActuators(context);
        CircuitSetup.EnsureProbesAsync(context, default).GetAwaiter().GetResult();

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

        // --- Seed de catálogos de forecasting ---
        if (!context.LotStatuses.Any())
        {
            context.LotStatuses.AddRange(
                new LotStatus { Name = "ACTIVO" },
                new LotStatus { Name = "COSECHADO" },
                new LotStatus { Name = "DESCARTADO" },
                new LotStatus { Name = "EN_PAUSA" }
            );
        }

        if (!context.CropTypes.Any())
        {
            context.CropTypes.Add(new CropType
            {
                Name = "Lechuga Baby Leaf",
                GddTarget = 300m,
                BaseTemperature = 4.5m,
                OptimalPhMin = 5.5m,
                OptimalPhMax = 6.5m,
                OptimalEcMin = 1.2m,
                OptimalEcMax = 1.8m,
                EstimatedDaysToHarvest = 25,
                YieldPerM2 = 3.0m,
                Description = "Lechuga de hoja cortada en estado juvenil (ciclo corto)"
            });
        }

        context.SaveChanges();

        // --- Seed de catálogos de lotes y plantas (plan 09 / LOT-02) ---
        // Valores de ARRANQUE calibrables, no verdades agronómicas: se ajustan
        // con datos reales del invernadero.
        var cultivoLote = context.CropTypes.First();

        if (!context.PhenologicalStages.Any())
        {
            context.PhenologicalStages.AddRange(
                new PhenologicalStage
                {
                    CropTypeId = cultivoLote.Id,
                    Name = "Establecimiento",
                    Description = "Arranque 0-150 GDD. Valor calibrable.",
                    Order = 1,
                    GddMin = 0, GddMax = 150,
                    EcMin = 0.8m, EcObjective = 1.0m, EcMax = 1.2m
                },
                new PhenologicalStage
                {
                    CropTypeId = cultivoLote.Id,
                    Name = "Crecimiento vegetativo",
                    Description = "Crecimiento 150-450 GDD. Valor calibrable.",
                    Order = 2,
                    GddMin = 150, GddMax = 450,
                    EcMin = 1.2m, EcObjective = 1.5m, EcMax = 1.8m
                },
                new PhenologicalStage
                {
                    CropTypeId = cultivoLote.Id,
                    Name = "Formación y madurez",
                    Description = "Tercera etapa (nombre confirmado en plan 09). 450-750 GDD. Valor calibrable.",
                    Order = 3,
                    GddMin = 450, GddMax = 750,
                    EcMin = 1.5m, EcObjective = 1.7m, EcMax = 1.8m
                });
        }

        if (!context.CommercialStages.Any())
        {
            context.CommercialStages.AddRange(
                new CommercialStage { CropTypeId = cultivoLote.Id, Name = "En desarrollo", Description = "Todavía no es cosechable." },
                new CommercialStage { CropTypeId = cultivoLote.Id, Name = "Candidata Baby Leaf", Description = "Score en banda candidata (60-79) o Baby Leaf sin confirmar obligatorios." },
                new CommercialStage { CropTypeId = cultivoLote.Id, Name = CommercialStageNames.BabyLeafApta, Description = "Score >= 80 y criterios obligatorios aprobados." },
                new CommercialStage { CropTypeId = cultivoLote.Id, Name = CommercialStageNames.CosechaConvencional, Description = "Ventana adulta con madurez confirmada." },
                new CommercialStage { CropTypeId = cultivoLote.Id, Name = "Riesgo / fuera de ventana", Description = "Anomalía, bolting, mal estado visual o sobremadurez." });
        }

        if (!context.BabyLeafConfigs.Any())
        {
            var babyLeafConfig = new BabyLeafConfig
            {
                CropTypeId = cultivoLote.Id,
                Name = "Baby Leaf Butterhead v1",
                Description = "Ventana GDD 250-450; candidata 60, Baby Leaf 80. Valores de arranque calibrables.",
                GddMin = 250, GddMax = 450,
                ScoreMinCandidate = 60, ScoreMinReady = 80,
                Version = "1.0"
            };
            context.BabyLeafConfigs.Add(babyLeafConfig);
            context.SaveChanges();

            context.BabyLeafCriteria.AddRange(
                new BabyLeafCriterion { BabyLeafConfigId = babyLeafConfig.Id, Name = "Ventana GDD", DataType = "GDD", Unit = "GDD", ValueMin = 250, ValueMax = 450, Weight = 20, IsMandatory = true },
                new BabyLeafCriterion { BabyLeafConfigId = babyLeafConfig.Id, Name = "Morfología / tamaño", DataType = "MORFOLOGIA", Unit = "score", Weight = 45, IsMandatory = true },
                new BabyLeafCriterion { BabyLeafConfigId = babyLeafConfig.Id, Name = "GrowthRate", DataType = "CRECIMIENTO", Unit = "%/día", Weight = 20, IsMandatory = false },
                new BabyLeafCriterion { BabyLeafConfigId = babyLeafConfig.Id, Name = "Estado visual", DataType = "VISUAL", Unit = "score", Weight = 15, IsMandatory = true });
        }

        context.SaveChanges();

        // --- Seed de catálogos de reglas de anomalías (plan 15 / ANO-01) ---
        // pH y CE: políticas activas; sus bandas operativas se RESUELVEN en evaluación
        // desde CropType/etapa fenológica (no se duplican valores acá).
        // Temperatura y humedad: NO tienen umbral agronómico aprobado → pendientes
        // (IsActive=false, nunca disparan). Física tomada del catálogo de IoT.
        if (!context.AnomalyRuleCatalogs.Any(c => c.CropTypeId == cultivoLote.Id))
        {
            var phPhysical = TelemetryValidationService.FindBand(AnomalyContract.SensorTypePh);
            var ecPhysical = TelemetryValidationService.FindBand(AnomalyContract.SensorTypeCe);
            var tempPhysical = TelemetryValidationService.FindBand(AnomalyContract.SensorTypeTemperatura);
            var humPhysical = TelemetryValidationService.FindBand(AnomalyContract.SensorTypeHumedad);

            context.AnomalyRuleCatalogs.AddRange(
                new AnomalyRuleCatalog
                {
                    CropTypeId = cultivoLote.Id,
                    Code = AnomalyContract.TypePhFueraDeBanda,
                    Name = "pH fuera de banda operativa",
                    SensorTypeName = AnomalyContract.SensorTypePh,
                    ConsecutiveToOpen = 2,
                    CooldownMinutes = 30,
                    IsActive = true,
                    Source = "crop-config",
                    Notes = "Banda operativa resuelta desde CropType.OptimalPh* (arranque 5,5–6,5; objetivo 6,0).",
                    PhysicalMin = phPhysical?.PhysicalMin,
                    PhysicalMax = phPhysical?.PhysicalMax
                },
                new AnomalyRuleCatalog
                {
                    CropTypeId = cultivoLote.Id,
                    Code = AnomalyContract.TypeCeFueraDeBanda,
                    Name = "CE fuera de banda operativa",
                    SensorTypeName = AnomalyContract.SensorTypeCe,
                    ConsecutiveToOpen = 2,
                    CooldownMinutes = 30,
                    IsActive = true,
                    Source = "stage-config",
                    Notes = "Banda operativa resuelta desde la etapa fenológica aplicada del lote (EcMin–EcMax, objetivo EcObjective).",
                    PhysicalMin = ecPhysical?.PhysicalMin,
                    PhysicalMax = ecPhysical?.PhysicalMax
                },
                new AnomalyRuleCatalog
                {
                    CropTypeId = cultivoLote.Id,
                    Code = AnomalyContract.TypeTemperaturaFueraDeBanda,
                    Name = "Temperatura ambiente fuera de banda",
                    SensorTypeName = AnomalyContract.SensorTypeTemperatura,
                    ConsecutiveToOpen = 2,
                    CooldownMinutes = 30,
                    IsActive = false,
                    Source = "pendiente",
                    Notes = "Sin umbral agronómico aprobado: pendiente de definición con el equipo; no se evalúa.",
                    PhysicalMin = tempPhysical?.PhysicalMin,
                    PhysicalMax = tempPhysical?.PhysicalMax
                },
                new AnomalyRuleCatalog
                {
                    CropTypeId = cultivoLote.Id,
                    Code = AnomalyContract.TypeHumedadFueraDeBanda,
                    Name = "Humedad ambiente fuera de banda",
                    SensorTypeName = AnomalyContract.SensorTypeHumedad,
                    ConsecutiveToOpen = 2,
                    CooldownMinutes = 30,
                    IsActive = false,
                    Source = "pendiente",
                    Notes = "Sin umbral agronómico aprobado: pendiente de definición con el equipo; no se evalúa.",
                    PhysicalMin = humPhysical?.PhysicalMin,
                    PhysicalMax = humPhysical?.PhysicalMax
                });
        }

        context.SaveChanges();

        // --- Seed de lote demo ---
        if (!context.Lots.Any())
        {
            SeedDemoLot(context);
        }

        // --- Seed de catálogo de costos/precios DEMO (módulo de optimización) ---
        // Valores etiquetados como "demo": la comparativa económica los muestra
        // como tales y nunca los presenta como precios reales (plan 16, OPT-03).
        // Sin precios vigentes la comparación es "No calculable" con el motivo.
        if (!context.CostPriceCatalogs.Any())
        {
            var cropType = context.CropTypes.FirstOrDefault();
            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            void AddPriceCost(string destination, string item, decimal value)
            {
                context.CostPriceCatalogs.Add(new HydroPilotWeb.Models.Optimization.CostPriceCatalog
                {
                    CropTypeId = cropType?.Id,
                    Destination = destination,
                    Item = item,
                    Value = value,
                    Currency = "ARS",
                    ValidFrom = today.AddDays(-30),
                    ValidUntil = today.AddDays(120),
                    Source = "demo",
                    CreatedAtUtc = DateTime.UtcNow
                });
            }

            // Destino Baby Leaf (ciclo corto, mayor densidad, sin trasplante individual).
            AddPriceCost("baby_leaf", "price_per_kg", 2600m);
            AddPriceCost("baby_leaf", "seed_cost_m2", 900m);
            AddPriceCost("baby_leaf", "nutrient_cost_m2", 320m);
            AddPriceCost("baby_leaf", "energy_cost_m2", 260m);
            AddPriceCost("baby_leaf", "transplant_cost_m2", 0m);

            // Destino convencional (ciclo más largo, trasplante y mayor consumo de energía).
            AddPriceCost("conventional", "price_per_kg", 1800m);
            AddPriceCost("conventional", "seed_cost_m2", 250m);
            AddPriceCost("conventional", "nutrient_cost_m2", 420m);
            AddPriceCost("conventional", "energy_cost_m2", 520m);
            AddPriceCost("conventional", "transplant_cost_m2", 480m);
        }

        context.SaveChanges();
    }

    /// <summary>
    /// Las 4 bombas de cada circuito (subir pH, bajar pH, nutrientes, agua) con límites para un tanque de 500 L.
    /// Solo agrega las que faltan: no pisa límites editados. Las claves técnicas son únicas en la Pi (el primer
    /// circuito de la Pi usa la clave base y los demás llevan sufijo, como CircuitSetup).
    /// </summary>
    public static void SeedActuators(HydroPilotDbContext context)
    {
        context.SaveChanges(); // circuitos recién agregados en este arranque necesitan su Id

        var circuits = context.Circuits.OrderBy(c => c.Id).Select(c => new { c.Id, c.NodeId }).ToList();
        var existing = context.Actuators.Select(a => new { a.CircuitId, a.NodeId, a.Kind, a.TechnicalKey }).ToList();
        foreach (var circuit in circuits)
        {
            var position = circuits.Where(c => c.NodeId == circuit.NodeId).Select(c => c.Id).ToList().IndexOf(circuit.Id) + 1;
            foreach (var actuator in ControlContract.DefaultActuators)
            {
                if (existing.Any(e => e.CircuitId == circuit.Id && e.Kind == actuator.Kind))
                    continue;

                var key = CircuitSetup.Key(actuator.Kind, position);
                if (existing.Any(e => e.NodeId == circuit.NodeId && e.TechnicalKey == key))
                    key = $"{actuator.Kind}-c{circuit.Id}";
                context.Actuators.Add(new Actuator
                {
                    NodeId = circuit.NodeId,
                    CircuitId = circuit.Id,
                    Kind = actuator.Kind,
                    TechnicalKey = key,
                    Name = actuator.Name,
                    Unit = actuator.Unit,
                    MaxPerCommand = actuator.MaxPerCommand,
                    MaxPerDay = actuator.MaxPerDay,
                    EffectPerUnit = actuator.Effect,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                });
            }
        }
        context.SaveChanges();
    }

    /// <summary>
    /// Siembra el Lote Demo 01 activo con su grilla de 60 plantas, plantado en un circuito libre.
    /// </summary>
    public static void SeedDemoLot(HydroPilotDbContext context)
    {
        var greenhouse = context.Greenhouses.FirstOrDefault();
        if (greenhouse == null) return;

        var cropType = context.CropTypes.FirstOrDefault();
        if (cropType == null) return;

        var status = context.LotStatuses.FirstOrDefault(s => s.Name == "ACTIVO");
        if (status == null) return;

        var demoLot = context.Lots.FirstOrDefault(l => l.Name == "Lote Demo 01");
        if (demoLot == null)
        {
            demoLot = new Lot
            {
                GreenhouseId = greenhouse.Id,
                CropTypeId = cropType.Id,
                StatusId = status.Id,
                Name = "Lote Demo 01",
                SowingDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-15)),
                PlantedAreaM2 = 4.5m,
                GridRows = 6,
                GridColumns = 10,
                CurrentPh = 6.0m,
                CurrentEc = 1.5m,
                BabyLeafHarvestTargetPercent = 70m,
                CreatedAt = DateTime.UtcNow
            };
            context.Lots.Add(demoLot);
            context.SaveChanges();
        }

        if (!context.BabyLeafConfigs.Any())
        {
            var babyLeafConfig = new BabyLeafConfig
            {
                CropTypeId = cropType.Id,
                Name = "Baby Leaf Butterhead v1",
                Description = "Ventana GDD 250-450; candidata 60, Baby Leaf 80. Valores de arranque calibrables.",
                GddMin = 250, GddMax = 450,
                ScoreMinCandidate = 60, ScoreMinReady = 80,
                Version = "1.0"
            };
            context.BabyLeafConfigs.Add(babyLeafConfig);
            context.SaveChanges();

            context.BabyLeafCriteria.AddRange(
                new BabyLeafCriterion { BabyLeafConfigId = babyLeafConfig.Id, Name = "Ventana GDD", DataType = "GDD", Unit = "GDD", ValueMin = 250, ValueMax = 450, Weight = 20, IsMandatory = true },
                new BabyLeafCriterion { BabyLeafConfigId = babyLeafConfig.Id, Name = "Morfología / tamaño", DataType = "MORFOLOGIA", Unit = "score", Weight = 45, IsMandatory = true },
                new BabyLeafCriterion { BabyLeafConfigId = babyLeafConfig.Id, Name = "GrowthRate", DataType = "CRECIMIENTO", Unit = "%/día", Weight = 20, IsMandatory = false },
                new BabyLeafCriterion { BabyLeafConfigId = babyLeafConfig.Id, Name = "Estado visual", DataType = "VISUAL", Unit = "score", Weight = 15, IsMandatory = true });
            context.SaveChanges();
        }

        if (!context.Plants.Any(p => p.LotId == demoLot.Id))
        {
            SeedDemoPlants(context, demoLot);
        }

        // Circuito libre (sin lote en curso) del invernadero; si no hay, el lote queda sin circuito.
        var circuit = context.Circuits
            .Where(c => c.GreenhouseId == greenhouse.Id && c.IsActive
                        && !context.CircuitAssignments.Any(a => a.CircuitId == c.Id && a.ValidUntilUtc == null))
            .OrderBy(c => c.Id)
            .FirstOrDefault();
        if (circuit != null && !context.CircuitAssignments.Any(a => a.LotId == demoLot.Id))
        {
            context.CircuitAssignments.Add(new CircuitAssignment
            {
                CircuitId = circuit.Id,
                LotId = demoLot.Id,
                ValidFromUtc = DateTime.UtcNow.AddDays(-60),
                ValidUntilUtc = null,
                Source = TelemetryContract.AssignmentSourceFixture,
                CreatedAt = DateTime.UtcNow
            });
            demoLot.CircuitId = circuit.Id;
            context.SaveChanges();
        }
        if (demoLot.CircuitId is { } demoCircuit)
        {
            foreach (var plant in context.Plants.Where(p => p.LotId == demoLot.Id && p.CircuitId == null))
                plant.CircuitId = demoCircuit;
            context.SaveChanges();
        }
    }

    /// <summary>
    /// Plantas demo del lote demo (datos NO productivos, etiquetados como "demo").
    /// El objetivo es mostrar la vista de lote (LOT-08) con estados comerciales
    /// variados y posiciones vacías, siempre respaldados por evaluaciones
    /// persistidas (nada se inventa en pantalla). El flujo diario puede volver a
    /// evaluarlas con reglas reales.
    /// </summary>
    private static void SeedDemoPlants(HydroPilotDbContext context, Lot demoLot)
    {
        var comm = context.CommercialStages.ToDictionary(s => s.Name);
        var pheno = context.PhenologicalStages.First(s => s.Name == "Crecimiento vegetativo");
        var config = context.BabyLeafConfigs.First();

        var now = DateTime.UtcNow;
        const decimal demoGdd = 320m;

        void AddPlant(
            int row, int col,
            string stageName, decimal score, decimal confidence,
            bool mandatoryMet, string? discardReason = null, bool harvested = false)
        {
            var plant = new Plant
            {
                LotId = demoLot.Id,
                Row = row,
                Column = col,
                PhenologicalStageId = pheno.Id,
                CommercialStageId = comm[stageName].Id,
                OperationalState = harvested ? PlantOperationalState.Cosechada
                                  : discardReason is not null ? PlantOperationalState.Descartada
                                  : PlantOperationalState.Activa,
                HarvestDate = harvested ? DateOnly.FromDateTime(now) : null,
                DiscardDate = discardReason is not null ? DateOnly.FromDateTime(now) : null,
                DiscardReason = discardReason,
                CreatedAtUtc = now
            };
            context.Plants.Add(plant);
            context.SaveChanges();

            context.BabyLeafEvaluations.Add(new BabyLeafEvaluation
            {
                PlantId = plant.Id,
                BabyLeafConfigId = config.Id,
                EvaluatedAtUtc = now,
                BabyLeafScore = score,
                Result = stageName,
                Confidence = confidence,
                GddAtEvaluation = demoGdd,
                MandatoryCriteriaMet = mandatoryMet,
                FoliarAreaUsed = 120m + score,
                LeafLengthUsed = 6.5m + score / 40m,
                GrowthRateUsed = 6m + score / 20m,
                ModelVersion = "BL-demo-1.0"
            });

            context.PlantStageHistories.Add(new PlantStageHistory
            {
                PlantId = plant.Id,
                NewCommercialStageId = comm[stageName].Id,
                NewPhenologicalStageId = pheno.Id,
                NewOperationalState = plant.OperationalState.ToString(),
                Source = "seed",
                Reason = "Dato demo: posición sembrada con evaluación de referencia",
                ChangedAtUtc = now
            });
        }

        // Fila 1: mezcla aptas / candidatas / en desarrollo.
        for (var col = 1; col <= 6; col++)
        {
            var (stage, score) = col switch
            {
                1 => (CommercialStageNames.BabyLeafApta, 88m),
                2 => (CommercialStageNames.BabyLeafApta, 91m),
                3 => (CommercialStageNames.BabyLeafApta, 85m),
                4 => ("Candidata Baby Leaf", 72m),
                5 => ("Candidata Baby Leaf", 66m),
                _ => ("En desarrollo", 45m)
            };
            AddPlant(1, col, stage, score, 0.9m, true);
        }

        // Fila 2: aptas y candidatas.
        AddPlant(2, 1, CommercialStageNames.BabyLeafApta, 89m, 0.92m, true);
        AddPlant(2, 2, CommercialStageNames.BabyLeafApta, 84m, 0.9m, true);
        AddPlant(2, 3, "Candidata Baby Leaf", 78m, 0.88m, true);
        AddPlant(2, 4, "Candidata Baby Leaf", 63m, 0.85m, true);
        AddPlant(2, 5, "En desarrollo", 52m, 0.8m, true);
        AddPlant(2, 6, "Riesgo / fuera de ventana", 30m, 0.7m, false, "Demo: bolting detectado");

        // Fila 3: apta sin confirmar obligatorios → candidata (caso documentado).
        AddPlant(3, 1, "Candidata Baby Leaf", 86m, 0.6m, false);
        AddPlant(3, 2, CommercialStageNames.BabyLeafApta, 90m, 0.93m, true);
        AddPlant(3, 3, "Candidata Baby Leaf", 70m, 0.87m, true);
        AddPlant(3, 4, "En desarrollo", 58m, 0.82m, true);

        // Fila 4: cosechada (congelada) y descartada (histórico).
        AddPlant(4, 1, CommercialStageNames.BabyLeafApta, 87m, 0.91m, true, harvested: true);
        AddPlant(4, 2, "Riesgo / fuera de ventana", 25m, 0.65m, false, "Demo: fuera de ventana por sobremadurez");
        AddPlant(4, 3, "En desarrollo", 41m, 0.78m, true);

        // Fila 5: en desarrollo y una apta.
        AddPlant(5, 1, "En desarrollo", 48m, 0.79m, true);
        AddPlant(5, 2, CommercialStageNames.BabyLeafApta, 83m, 0.89m, true);

        // Fila 6: una planta.
        AddPlant(6, 1, "En desarrollo", 39m, 0.75m, true);

        // El resto de las 60 posiciones quedan vacías (gris claro en el mapa).
    }
}
