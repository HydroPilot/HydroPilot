using HydroPilotWeb.Models;
using HydroPilotWeb.Services;
using HydroPilotWeb.Models.Control;
using HydroPilotWeb.Services.Anomalies;
using HydroPilotWeb.Services.Control;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.SimulationEnvironment;
using HydroPilotWeb.Services.SimulationEnvironment.Model;
using Xunit.Abstractions;

namespace HydroPilotWeb.Tests.Unit;

/// <summary>Mundo de prueba igual al entorno de simulación (catálogos, una Pi con 2 mesadas densas de 10 × 20 y 5 de Adultas de 4 × 5, usuarios).</summary>
internal static class SimulationWorlds
{
    public static SimWorld Seeded()
    {
        var crop = new CropType
        {
            Id = 1, Name = "Lechuga Baby Leaf", GddTarget = 300m, BaseTemperature = 4.5m,
            OptimalPhMin = 5.5m, OptimalPhMax = 6.5m, OptimalEcMin = 1.2m, OptimalEcMax = 1.8m, YieldPerM2 = 3m,
        };
        PhenologicalStage[] stages =
        [
            new() { Id = 1, CropTypeId = 1, Name = "Establecimiento", Order = 1, GddMin = 0, GddMax = 150, EcMin = 0.8m, EcObjective = 1.0m, EcMax = 1.2m },
            new() { Id = 2, CropTypeId = 1, Name = "Crecimiento vegetativo", Order = 2, GddMin = 150, GddMax = 450, EcMin = 1.2m, EcObjective = 1.5m, EcMax = 1.8m },
            new() { Id = 3, CropTypeId = 1, Name = "Formación y madurez", Order = 3, GddMin = 450, GddMax = 750, EcMin = 1.5m, EcObjective = 1.7m, EcMax = 1.8m },
        ];
        var commercial = new Dictionary<string, int>
        {
            [CommercialStageNames.EnDesarrollo] = 1,
            [CommercialStageNames.CandidataBabyLeaf] = 2,
            [CommercialStageNames.BabyLeafApta] = 3,
            [CommercialStageNames.CosechaConvencional] = 4,
            [CommercialStageNames.RiesgoFueraDeVentana] = 5,
        };
        var config = new BabyLeafConfig { Id = 1, CropTypeId = 1, Name = "Baby Leaf Butterhead v1", GddMin = 250, GddMax = 450, ScoreMinCandidate = 60, ScoreMinReady = 80 };
        BabyLeafCriterion[] criteria =
        [
            new() { Id = 1, BabyLeafConfigId = 1, Name = "Ventana GDD", DataType = "GDD", ValueMin = 250, ValueMax = 450, Weight = 20, IsMandatory = true },
            new() { Id = 2, BabyLeafConfigId = 1, Name = "Morfología / tamaño", DataType = "MORFOLOGIA", Weight = 45, IsMandatory = true },
            new() { Id = 3, BabyLeafConfigId = 1, Name = "GrowthRate", DataType = "CRECIMIENTO", Weight = 20 },
            new() { Id = 4, BabyLeafConfigId = 1, Name = "Estado visual", DataType = "VISUAL", Weight = 15, IsMandatory = true },
        ];
        var statuses = new Dictionary<string, int>
        {
            [LotStatusNames.Activo] = 1, [LotStatusNames.Cosechado] = 2, [LotStatusNames.Descartado] = 3, [LotStatusNames.EnPausa] = 4,
        };
        var rules = new Dictionary<string, AnomalyRuleCatalog>
        {
            [AnomalyContract.SensorTypePh] = new() { Id = 1, CropTypeId = 1, Code = AnomalyContract.TypePhFueraDeBanda, Name = "pH fuera de banda operativa",
                SensorTypeName = AnomalyContract.SensorTypePh, ConsecutiveToOpen = 2, CooldownMinutes = 30, IsActive = true },
            [AnomalyContract.SensorTypeCe] = new() { Id = 2, CropTypeId = 1, Code = AnomalyContract.TypeCeFueraDeBanda, Name = "CE fuera de banda operativa",
                SensorTypeName = AnomalyContract.SensorTypeCe, ConsecutiveToOpen = 2, CooldownMinutes = 30, IsActive = true },
        };

        // Una Pi (nodo 1) con 7 circuitos: 2 densos (A, B) y 5 de Adultas (C–G); la temperatura y la humedad del
        // invernadero las manda el primero.
        var benches = new List<SimBench>();
        for (var i = 0; i < 7; i++)
        {
            var adult = i >= 2;
            const int nodeId = 1;
            var circuitId = i + 1;
            var suffix = i == 0 ? string.Empty : $"-{i + 1}";
            var sensors = new Dictionary<string, SimSensor>
            {
                ["pH"] = new(i * 4 + 1, "pH", 1, "pH", $"ph-solucion{suffix}"),
                ["CE"] = new(i * 4 + 2, "CE", 2, "mS/cm", $"ec-solucion{suffix}"),
            };
            if (i == 0)
            {
                sensors["Temperatura"] = new(3, "Temperatura", 3, "°C", "temp-ambiente");
                sensors["Humedad"] = new(4, "Humedad", 4, "%", "hum-ambiente");
            }
            var actuators = ControlContract.DefaultActuators
                .Select((a, j) => new Actuator
                {
                    Id = i * 4 + j + 1, NodeId = nodeId, CircuitId = circuitId, Kind = a.Kind, TechnicalKey = a.Kind + suffix, Name = a.Name,
                    Unit = a.Unit, MaxPerCommand = a.MaxPerCommand, MaxPerDay = a.MaxPerDay, EffectPerUnit = a.Effect, IsActive = true,
                })
                .ToList();
            benches.Add(new SimBench(nodeId, "rpi-inv-01", ((char)('A' + i)).ToString(), sensors, actuators, circuitId,
                adult ? $"Adultas {i - 1}" : $"Circuito {circuitId}", adult ? CircuitKinds.Adult : CircuitKinds.Dense,
                adult ? 4 : 10, adult ? 5 : 20));
        }

        var producer = new SimUser(2, "carla.gomez@hydropilot.test", "Carla Gómez", "Productor");
        var @operator = new SimUser(3, "diego.ruiz@hydropilot.test", "Diego Ruiz", "Operador");
        return new SimWorld(1, TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"), crop, stages, commercial, config, criteria,
            statuses, rules, benches, producer, @operator,
            [new SimUser(1, "admin@hydropilot.local", "Admin", "Administrador"), producer, @operator],
            30m, 280m);
    }
}

/// <summary>Un historial de 4 meses generado una sola vez para todas las pruebas.</summary>
public sealed class ShortHistory
{
    public static readonly DateTime Now = new(2026, 10, 20, 18, 0, 0, DateTimeKind.Utc);

    public ShortHistory() =>
        Data = HistoryGenerator.Generate(SimulationWorlds.Seeded(), new SimulationEnvironmentOptions { HistoryMonths = 4 }, Now);

    public HistoryData Data { get; }
}

/// <summary>
/// Entorno de simulación: el modelo del tanque y las plantas, y el historial generado (lógica pura, sin base).
/// </summary>
public class SimulationEnvironmentTests : IClassFixture<ShortHistory>
{
    private readonly HistoryData _data;
    private readonly ITestOutputHelper _output;

    public SimulationEnvironmentTests(ShortHistory history, ITestOutputHelper output)
    {
        _data = history.Data;
        _output = output;
    }

    private static readonly DateTime Now = ShortHistory.Now;

    [Fact]
    public void Lotes_de_cada_mesada_uno_detras_del_otro_y_los_en_curso_en_su_etapa()
    {
        foreach (var node in _data.Assignments.GroupBy(a => a.CircuitId))
        {
            var ordered = node.OrderBy(a => a.ValidFromUtc).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                Assert.NotNull(ordered[i - 1].ValidUntilUtc);
                Assert.True(ordered[i - 1].ValidUntilUtc <= ordered[i].ValidFromUtc, "dos lotes a la vez en la misma mesada");
            }
            if (node.Key is 1 or 2)
                Assert.Null(ordered[^1].ValidUntilUtc); // las mesadas densas tienen su lote en curso
        }

        // Los en curso de las mesadas densas (puede haber otro con sus adultas en las de Adultas).
        var open = _data.Lots.Where(l => l.StatusId == 1).OrderByDescending(l => l.SowingDate).Take(2).OrderBy(l => l.SowingDate).ToList();
        Assert.Equal(2, open.Count);
        var today = new DateOnly(2026, 10, 20);
        Assert.Equal(today.AddDays(-12), open[0].SowingDate); // mesada A: cerca de la ventana Baby Leaf
        Assert.Equal(today.AddDays(-4), open[1].SowingDate);  // mesada B: recién sembrada
        Assert.InRange(open[0].AccumulatedGdd!.Value, 150m, 250m);
    }

    [Fact]
    public void Las_lecturas_tienen_el_lote_de_su_asignacion_y_valores_posibles()
    {
        var assignments = _data.Assignments.ToDictionary(a => a.Id);
        foreach (var reading in _data.Readings)
        {
            if (reading.CircuitAssignmentId is { } id)
            {
                var a = assignments[id];
                Assert.Equal(a.LotId, reading.LotId);
                Assert.True(reading.ObservedAtUtc >= a.ValidFromUtc && (a.ValidUntilUtc is null || reading.ObservedAtUtc < a.ValidUntilUtc),
                    $"lectura {reading.ExternalReadingId} del {reading.ObservedAtUtc:o} fuera de la asignación {a.Id} ({a.ValidFromUtc:o} – {a.ValidUntilUtc:o})");
                Assert.Equal(TelemetryContract.ResultAceptada, reading.IngestionResult);
            }
            else
            {
                Assert.Null(reading.LotId);
                // Temperatura y humedad (sensores 3 y 4) son del invernadero: aceptadas sin lote.
                Assert.Equal(reading.SensorId is 3 or 4 ? TelemetryContract.ResultAceptada : TelemetryContract.ResultSinLote,
                    reading.IngestionResult);
            }
        }

        var ph = _data.Readings.Where(r => r.SensorId % 4 == 1).Select(r => r.Value).ToList();
        var ec = _data.Readings.Where(r => r.SensorId % 4 == 2).Select(r => r.Value).ToList();
        Assert.InRange(ph.Average(), 5.7m, 6.4m);
        Assert.InRange(ec.Min(), 0.6m, 1.2m);
        Assert.InRange(ec.Max(), 1.2m, 2.2m);
        Assert.All(_data.Readings, r => Assert.True(r.Value is >= 0m and <= 100m));
        Assert.Equal(_data.Readings.Count, _data.Readings.Select(r => (r.NodeId, r.ExternalReadingId)).Distinct().Count());
        Assert.Equal(_data.Batches.Count, _data.Batches.Select(b => (b.NodeId, b.BatchId)).Distinct().Count());
    }

    [Fact]
    public void Las_dosis_automaticas_salen_del_motor_y_tienen_su_efecto_medido()
    {
        var auto = _data.Commands.Where(c => c.Source == ControlContract.SourceAuto).ToList();
        Assert.NotEmpty(auto);
        Assert.All(auto, c =>
        {
            Assert.Equal(ControlContract.ControllerAuto, c.ControllerVersion);
            Assert.InRange(c.Strength!.Value, 0.01m, 0.9m); // promedio difuso de Nula 0 · Suave 0,3 · Media 0,6 · Fuerte 0,9
            Assert.NotNull(c.ExpectedEffect);
        });

        var measured = _data.Commands.Where(c => c.Effective is not null).ToList();
        Assert.True(measured.Count > auto.Count / 2);
        Assert.True(measured.Count(c => c.Effective == true) > measured.Count * 0.7);

        // Pausa por 3 dosis sin efecto (bomba tapada o bidón vacío) con su alerta, resuelta al reanudar.
        Assert.NotEmpty(_data.Pauses);
        Assert.All(_data.Pauses.Where(p => p.ResumedAtUtc is not null), p =>
            Assert.Contains(_data.Alerts, a => a.Type == "FallaDosificacion" && a.IsResolved
                                               && a.Fingerprint.StartsWith($"control-pause-{p.CircuitId}-{p.Parameter}-")));
    }

    [Fact]
    public void Lotes_cerrados_con_su_resultado_y_rendimiento()
    {
        var closed = _data.Lots.Where(l => l.StatusId is 2 or 3).ToList();
        Assert.NotEmpty(closed);
        foreach (var lot in closed)
        {
            var plants = _data.Plants.Where(p => p.LotId == lot.Id).ToList();
            Assert.DoesNotContain(plants, p => p.OperationalState == PlantOperationalState.Activa);
            if (lot.StatusId != 2)
                continue;
            var babyLeaf = plants.Count(p => p.OperationalState == PlantOperationalState.Cosechada && p.CommercialStageId != 4);
            // Las trasplantadas son "Adulta" desde el trasplante: cuentan las cosechadas.
            var conventional = plants.Count(p => p.OperationalState == PlantOperationalState.Cosechada && p.CommercialStageId == 4);
            Assert.Equal(Math.Round((babyLeaf * 30m + conventional * 280m) / 1000m, 2), lot.ActualYieldKg);
            Assert.Equal(plants.Max(p => p.HarvestDate), lot.ActualHarvestDate);
            Assert.NotNull(lot.HarvestBabyLeafPricePerKg);
        }

        // Una evaluación por planta y día, con su imagen y su análisis.
        Assert.Equal(_data.Evaluations.Count, _data.Images.Count);
        Assert.Equal(_data.Images.Count, _data.Analyses.Count);
        var perDay = _data.Evaluations.GroupBy(e => (e.PlantId, DateOnly.FromDateTime(e.EvaluatedAtUtc))).Max(g => g.Count());
        Assert.Equal(1, perDay);
    }

    [Fact]
    public void Misma_semilla_mismo_historial()
    {
        var again = HistoryGenerator.Generate(SimulationWorlds.Seeded(), new SimulationEnvironmentOptions { HistoryMonths = 4 }, Now);
        Assert.Equal(_data.Readings.Count, again.Readings.Count);
        Assert.Equal(_data.Commands.Select(c => (c.CreatedAtUtc, c.Amount)), again.Commands.Select(c => (c.CreatedAtUtc, c.Amount)));
        Assert.Equal(_data.Evaluations.Sum(e => e.BabyLeafScore), again.Evaluations.Sum(e => e.BabyLeafScore));
    }

    [Fact]
    public void Tanque_dosis_dilucion_y_absorcion()
    {
        var tank = new NutrientTank(500, 6.5, 1.5);

        tank.Dose(ControlContract.KindPhDown, 50); // 0,1 ml/L × 2,2 = −0,22 pH
        tank.Step(600, Uptake.None);
        Assert.Equal(6.28, tank.Ph, 2);

        tank.Dose(ControlContract.KindNutrientA, 200); // 0,4 ml/L × 0,26 = +0,104 CE
        tank.Step(600, Uptake.None);
        Assert.Equal(1.604, tank.Ec, 3);

        // El agua de red diluye y lo que pasa la capacidad rebalsa.
        tank.Dose(ControlContract.KindWater, 50);
        tank.Step(600, Uptake.None);
        Assert.Equal(500, tank.Liters, 3);
        Assert.Equal((1.604 * 500 + 0.2 * 50) / 550, tank.Ec, 2);

        // Las plantas toman agua: el nivel baja; con calor la CE sube, con frío baja.
        var hot = new NutrientTank(500, 6, 1.5);
        var cold = new NutrientTank(500, 6, 1.5);
        for (var hour = 0; hour < 24; hour++)
        {
            hot.Step(3600, LettuceUptake.For(200, 300, 3600, hour, 31, 50, hot.Ec, hot.Liters));
            cold.Step(3600, LettuceUptake.For(200, 300, 3600, hour, 13, 80, cold.Ec, cold.Liters));
        }
        Assert.True(hot.Liters < 490 && hot.Ec > 1.5, $"calor: {hot.Liters:0} L, CE {hot.Ec:0.000}");
        Assert.True(cold.Ec < 1.5, $"frío: CE {cold.Ec:0.000}");
        Assert.InRange(cold.Ph - 6, 0.05, 0.4); // el nitrato sube el pH
    }

    [Fact]
    public void Absorcion_usa_los_litros_de_Simulacion()
    {
        Assert.Equal((double)Services.Simulation.NutrientConsumptionRules.WaterBeforeBabyLeaf, LettuceUptake.WaterBeforeBabyLeaf);
        Assert.Equal((double)Services.Simulation.NutrientConsumptionRules.WaterDuringBabyLeaf, LettuceUptake.WaterDuringBabyLeaf);
        Assert.Equal((double)Services.Simulation.NutrientConsumptionRules.WaterConventional, LettuceUptake.WaterConventional);

        // 200 plantas (una mesada densa) en la ventana, día normal: 200 × 0,055 = 11 L en el día (casi todo de día).
        var day = Enumerable.Range(0, 24).Sum(h => LettuceUptake.For(200, 300, 3600, h + 0.5, 20, 65, 1.5, 500).WaterLiters);
        Assert.Equal(11, day, 0);
    }

    [Fact]
    public void Clima_de_Buenos_Aires_adentro_del_invernadero()
    {
        var january = GreenhouseClimate.Inside(GreenhouseClimate.Normal(new DateOnly(2026, 1, 15)), 0);
        var july = GreenhouseClimate.Inside(GreenhouseClimate.Normal(new DateOnly(2026, 7, 15)), 0);
        Assert.InRange(january.Max, 31, 32.1);
        Assert.InRange(july.Min, 9, 10);
        Assert.Equal((double)GddService.DailyGdd(30m, 18m, 4.5m), GreenhouseClimate.DailyGdd(32, 18), 6);

        var noon = GreenhouseClimate.NormalAt(new DateTime(2026, 10, 20, 15, 0, 0));
        var night = GreenhouseClimate.NormalAt(new DateTime(2026, 10, 20, 6, 0, 0));
        Assert.True(noon.Temperature > night.Temperature + 8);
        Assert.True(noon.Humidity < night.Humidity);
    }

    [Fact(Skip = "Diagnóstico manual: imprime el historial de 24 meses lote por lote (~1 min).")]
    public void Diagnostico_del_historial_completo()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var data = HistoryGenerator.Generate(SimulationWorlds.Seeded(), new SimulationEnvironmentOptions { HistoryMonths = 24 }, Now);
        _output.WriteLine($"Generado en {watch.Elapsed.TotalSeconds:0.0} s");
        _output.WriteLine($"Lotes {data.Lots.Count}, plantas {data.Plants.Count}, evaluaciones {data.Evaluations.Count}, lecturas {data.Readings.Count}, " +
                          $"batches {data.Batches.Count}, rechazos {data.Rejections.Count}, comandos {data.Commands.Count}, niveles {data.Setpoints.Count}, " +
                          $"recetas {data.Recipes.Count}, pausas {data.Pauses.Count}, anomalías {data.Anomalies.Count}, " +
                          $"alertas {data.Alerts.Count}, entregas {data.Deliveries.Count}, intentos {data.Attempts.Count}");
        foreach (var line in data.Story)
            _output.WriteLine(line);

        var apt = data.StageHistories.Where(h => h.NewCommercialStageId == 3).Select(h => h.PlantId).ToHashSet();
        var means = data.Readings.Where(r => r.LotId != null && (r.SensorId % 4 == 1 || r.SensorId % 4 == 2))
            .GroupBy(r => (r.LotId!.Value, r.SensorId % 4 == 1))
            .ToDictionary(g => g.Key, g => g.Average(r => r.Value));
        foreach (var lot in data.Lots)
        {
            var plants = data.Plants.Where(p => p.LotId == lot.Id).ToList();
            var bl = plants.Count(p => p.OperationalState == PlantOperationalState.Cosechada && p.CommercialStageId != 4);
            var conv = plants.Count(p => p.CommercialStageId == 4);
            var discarded = plants.Count(p => p.OperationalState == PlantOperationalState.Descartada);
            var commands = data.Commands.Where(c => c.LotId == lot.Id).ToList();
            var recipe = data.Recipes.FirstOrDefault(r => r.LotId == lot.Id);
            _output.WriteLine($"{lot.Name,-22} st {lot.StatusId} siembra {lot.SowingDate:dd/MM/yy} GDD {lot.AccumulatedGdd,6:0} " +
                              $"BL {bl,2} conv {conv,2} desc {discarded,2} aptas {plants.Count(p => apt.Contains(p.Id)),2}/{plants.Count} " +
                              $"kg {lot.ActualYieldKg,5} dosis {commands.Count,3} (auto {commands.Count(c => c.Source == "AUTO"),3}) " +
                              $"anom {data.Anomalies.Count(a => a.LotId == lot.Id),2} " +
                              $"pH {means.GetValueOrDefault((lot.Id, true)):0.00} CE {means.GetValueOrDefault((lot.Id, false)):0.00} " +
                              $"receta {recipe?.PhOffset:+0.00;-0.00;0}/{recipe?.EcOffset:+0.00;-0.00;0} ({recipe?.SimilarLots} parecidos)");
        }

        foreach (var lot in data.Lots)
        {
            var reasons = data.Plants.Where(p => p.LotId == lot.Id && p.DiscardReason != null)
                .GroupBy(p => p.DiscardReason).Select(g => $"{g.Key} ×{g.Count()}");
            var anomalies = data.Anomalies.Where(a => a.LotId == lot.Id)
                .GroupBy(a => $"{a.Type[..2]}:{a.Severity[..1]}{(a.OperationalMin is { } min && a.ObservedValue < min ? "↓" : "↑")}")
                .Select(g => $"{g.Key}×{g.Count()}");
            var recipe = data.Recipes.FirstOrDefault(r => r.LotId == lot.Id);
            _output.WriteLine($"{lot.Name}: {string.Join(", ", reasons)} | {string.Join(" ", anomalies)} | historial {recipe?.HistoryPhOffset}/{recipe?.HistoryEcOffset} " +
                              $"ev {recipe?.Evidence} conf {recipe?.Trust} tend {recipe?.ScoreTrend} · {recipe?.Reason}");
        }

        Assert.NotEmpty(data.Lots);
    }
}
