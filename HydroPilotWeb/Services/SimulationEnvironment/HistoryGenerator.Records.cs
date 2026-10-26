using HydroPilotWeb.Models;
using HydroPilotWeb.Models.Notifications;
using HydroPilotWeb.Models.Optimization;
using HydroPilotWeb.Services.Anomalies;
using HydroPilotWeb.Services.Optimization;
using HydroPilotWeb.Services.SimulationEnvironment.Model;

namespace HydroPilotWeb.Services.SimulationEnvironment;

/// <summary>
/// Lo que el historial deja en Anomalías y Notificaciones, con las reglas de cada módulo: episodios por lecturas fuera
/// de la banda (2 seguidas abren, 2 normales cierran, 30 min de cooldown), avisos de cosecha y las alertas con sus
/// entregas; además precios, clima y pronósticos.
/// </summary>
public sealed partial class HistoryGenerator
{
    // ------------------------------------------------------------------
    // Anomalías (como AnomalyEventService, lectura por lectura)
    // ------------------------------------------------------------------

    private void TrackAnomaly(BenchRun bench, LotRun run, string type, SensorReading reading)
    {
        if (!_w.AnomalyRules.TryGetValue(type, out var catalog) || !catalog.IsActive)
            return;
        var band = AnomalyBandResolver.ResolveOperative(_w.Crop, run.Stage, type, catalog);
        if (band is null)
            return;

        var at = reading.ObservedAtUtc;
        var outOfBand = AnomalyEvaluator.IsOutOfBand(reading.Value, band);
        run.OpenEpisodes.TryGetValue(type, out var episode);

        if (!outOfBand)
        {
            if (episode is null)
                return;
            episode.Normals++;
            if (episode.Normals >= 2)
                ResolveEpisode(run, type, episode, at.AddSeconds(20), "recuperación: 2 lecturas normales consecutivas");
            return;
        }

        if (episode is not null)
        {
            var e = episode.Event;
            episode.Normals = 0;
            e.ReadingCount++;
            e.LastObservedAtUtc = at;
            e.ObservedValue = reading.Value;
            e.PeakValue = AnomalyEvaluator.Peak(e.PeakValue, reading.Value, band);
            if (e.Status == AnomalyContract.StatusSeguimiento && e.ReadingCount >= catalog.ConsecutiveToOpen)
                OpenEpisode(run, episode, at.AddSeconds(20));
            else if (e.Status == AnomalyContract.StatusAbierta && e.AcknowledgedAtUtc is null && at - e.FirstObservedAtUtc > TimeSpan.FromMinutes(90)
                     && ToLocal(at).Hour is >= 8 and < 19)
            {
                e.AcknowledgedAtUtc = at.AddMinutes(_rng.Next(1, 9));
                e.Status = AnomalyContract.StatusReconocida;
            }
            return;
        }

        if (run.LastResolved.TryGetValue(type, out var resolved) && at < resolved.AddMinutes(catalog.CooldownMinutes))
            return;

        var anomaly = new AnomalyEvent
        {
            Id = ++_anomalyId,
            Type = catalog.Code,
            LotId = run.Lot.Id,
            GreenhouseId = _w.GreenhouseId,
            NodeId = bench.Bench.NodeId,
            SensorId = reading.SensorId,
            Severity = AnomalyContract.SeverityAdvertencia,
            ObservedValue = reading.Value,
            PeakValue = reading.Value,
            TargetValue = band.Target,
            OperationalMin = band.OperationalMin,
            OperationalMax = band.OperationalMax,
            RuleCode = catalog.Code,
            RuleDescription = AnomalyRuleDescriptions.Describe(catalog.Name, type, band),
            FirstObservedAtUtc = at,
            LastObservedAtUtc = at,
            ReadingCount = 1,
            Status = AnomalyContract.StatusSeguimiento,
            Fingerprint = AnomalyFingerprint.Compute(run.Lot.Id, reading.SensorId, catalog.Code, at),
            Origin = AnomalyContract.OriginTelemetria,
            CreatedAtUtc = at.AddSeconds(20),
        };
        _data.Anomalies.Add(anomaly);
        var created = new EpisodeRun(anomaly, new ResolvedAnomalyRule(catalog.Code, catalog.Name, catalog.ConsecutiveToOpen, catalog.CooldownMinutes));
        run.OpenEpisodes[type] = created;
        if (catalog.ConsecutiveToOpen <= 1)
            OpenEpisode(run, created, at.AddSeconds(20));
    }

    /// <summary>El episodio pasa a Abierta (crítica): alerta de desbalance químico.</summary>
    private void OpenEpisode(LotRun run, EpisodeRun episode, DateTime at)
    {
        var e = episode.Event;
        e.Status = AnomalyContract.StatusAbierta;
        e.Severity = AnomalyContract.SeverityCritica;
        episode.Alert = AddAlert(NotificationTypes.DesbalanceQuimico, NotificationSeverities.Critica,
            $"Anomalía crítica en {run.Lot.Name}: {e.Type}",
            e.RuleDescription ?? $"El parámetro {e.Type} registró valor {e.ObservedValue:F2} fuera de rango operativo.",
            $"anomaly-event-{e.Id}", run.Lot.Id, at.AddSeconds(15));
    }

    private void ResolveEpisode(LotRun run, string type, EpisodeRun episode, DateTime at, string reason)
    {
        var e = episode.Event;
        e.Status = AnomalyContract.StatusResuelta;
        e.ResolvedAtUtc = at;
        e.ResolutionReason = reason;
        run.LastResolved[type] = at;
        run.OpenEpisodes.Remove(type);
        if (episode.Alert is { } alert)
        {
            alert.IsResolved = true;
            alert.ResolvedAtUtc = at.AddSeconds(15);
        }
    }

    /// <summary>Motivo de riesgo de las plantas con un episodio abierto o reconocido (AnomalyLotRiskEvaluator).</summary>
    private string? OpenEpisodeReason(LotRun run)
    {
        var open = run.OpenEpisodes.Values
            .Where(e => e.Event.Status is AnomalyContract.StatusAbierta or AnomalyContract.StatusReconocida)
            .OrderBy(e => e.Event.FirstObservedAtUtc)
            .ToList();
        if (open.Count == 0)
            return null;
        var first = open[0].Event;
        return open.Count > 1
            ? $"{open.Count} episodios abiertos ({open[0].Rule.Name}: valor {first.ObservedValue:0.##}, objetivo {first.TargetValue:0.##}, desde {first.FirstObservedAtUtc:g})"
            : $"{open[0].Rule.Name}: valor {first.ObservedValue:0.##} fuera de banda"
              + (first.TargetValue.HasValue ? $", objetivo {first.TargetValue:0.##}" : string.Empty)
              + $"; desde {first.FirstObservedAtUtc:g}";
    }

    /// <summary>Al cerrar el lote, lo abierto se resuelve (Anomalías) y el aviso de cosecha se da por resuelto.</summary>
    private void BuildAnomalies(LotRun run, DateTime t)
    {
        foreach (var (type, episode) in run.OpenEpisodes.ToList())
            ResolveEpisode(run, type, episode, t.AddSeconds(30), "lote finalizado (cosechado o descartado): no genera más anomalías operativas");

        if (run.HarvestAlert is { IsResolved: false } harvest)
        {
            harvest.IsResolved = true;
            harvest.ResolvedAtUtc = t;
        }
    }

    // ------------------------------------------------------------------
    // Aviso de cosecha inminente (una vez por día)
    // ------------------------------------------------------------------

    /// <summary>Días antes del óptimo Baby Leaf en que se avisa la cosecha.</summary>
    private const int HarvestAlertDays = 3;

    /// <summary>Con aptas o candidatas y el óptimo Baby Leaf a 3 días o menos: un aviso de cosecha inminente por lote.</summary>
    private void HarvestAlerts(LotRun run, DateTime t, DateOnly date)
    {
        var gdd = run.Gdd;
        if (run.HarvestAlert is not null || gdd >= Lotes.CycleTimelineLogic.BabyLeafOptimumGdd)
            return;

        var ready = run.Plants.Count(p => p.Active
            && (p.Plant.CommercialStageId == _w.CommercialStageIds[Lotes.CommercialStageNames.BabyLeafApta]
                || p.Plant.CommercialStageId == _w.CommercialStageIds[Lotes.CommercialStageNames.CandidataBabyLeaf]));
        var perDay = Math.Max(1m, gdd / Math.Max(1, date.DayNumber - run.Lot.SowingDate.DayNumber));
        var days = (int)Math.Ceiling((Lotes.CycleTimelineLogic.BabyLeafOptimumGdd - gdd) / perDay);
        if (ready == 0 || days is <= 0 or > HarvestAlertDays)
            return;

        run.HarvestAlert = AddAlert(NotificationTypes.CosechaInminente,
            days <= 1 ? NotificationSeverities.Critica : NotificationSeverities.Alta,
            $"Cosecha inminente: {run.Lot.Name}",
            $"El lote '{run.Lot.Name}' alcanzará la madurez de cosecha en aproximadamente {days} día(s). Se recomienda planificar logística.",
            $"harvest-imminent-lot-{run.Lot.Id}", run.Lot.Id, t.AddMinutes(16));
    }

    // ------------------------------------------------------------------
    // Notificaciones (como NotificationService.CreateAlertAsync)
    // ------------------------------------------------------------------

    private NotificationAlert AddAlert(string type, string severity, string title, string message, string fingerprint, int? lotId, DateTime at)
    {
        var alert = new NotificationAlert
        {
            Id = ++_alertId,
            Type = type,
            Severity = severity,
            Title = title.Length > 200 ? title[..200] : title,
            Message = message.Length > 2000 ? message[..2000] : message,
            Fingerprint = fingerprint,
            LotId = lotId,
            CreatedAtUtc = at,
        };
        _data.Alerts.Add(alert);

        var old = _nowUtc - at > TimeSpan.FromDays(2);
        foreach (var user in _w.Recipients)
        {
            var read = old || _rng.NextDouble() < 0.4;
            _data.Deliveries.Add(new NotificationDelivery
            {
                Id = ++_deliveryId,
                AlertId = alert.Id,
                UserId = user.Id,
                Channel = NotificationChannels.Internal,
                Status = DeliveryStatuses.Sent,
                Recipient = user.Email,
                SentAtUtc = at,
                IsRead = read,
                ReadAtUtc = read ? at.AddHours(1 + _rng.NextDouble() * 18) : null,
                CreatedAtUtc = at,
            });

            // La productora recibe por correo lo Alto y Crítico (Ajustes > Notificaciones).
            if (user.Id == _w.Producer.Id && severity is NotificationSeverities.Alta or NotificationSeverities.Critica)
            {
                var failedFirst = _rng.NextDouble() < 0.04;
                var delivery = new NotificationDelivery
                {
                    Id = ++_deliveryId,
                    AlertId = alert.Id,
                    UserId = user.Id,
                    Channel = NotificationChannels.Email,
                    Status = DeliveryStatuses.Sent,
                    Recipient = user.Email,
                    Attempts = failedFirst ? 2 : 1,
                    LastError = failedFirst ? "SMTP 421: servicio no disponible, reintentar más tarde" : null,
                    SentAtUtc = at.AddSeconds(failedFirst ? 75 : 15),
                    CreatedAtUtc = at,
                };
                _data.Deliveries.Add(delivery);
                if (failedFirst)
                {
                    _data.Attempts.Add(new NotificationAttempt
                    {
                        Id = ++_attemptId, DeliveryId = delivery.Id, AttemptNumber = 1, AttemptedAtUtc = at.AddSeconds(15),
                        Success = false, ErrorMessage = delivery.LastError,
                    });
                }
                _data.Attempts.Add(new NotificationAttempt
                {
                    Id = ++_attemptId, DeliveryId = delivery.Id, AttemptNumber = failedFirst ? 2 : 1, AttemptedAtUtc = delivery.SentAtUtc!.Value,
                    Success = true,
                });
            }
        }
        return alert;
    }

    // ------------------------------------------------------------------
    // Precios y parámetros
    // ------------------------------------------------------------------

    /// <summary>Precio de nutrientes por litro de concentrado A+B (kit Acquagarden 1 L A + 1 L B ≈ $30.870, 2026).</summary>
    private const decimal NutrientPricePerLiter = 15_000m;

    /// <summary>
    /// Precio por kg de cada destino a una fecha (pesos nominales, supuesto a partir de CEPA y góndolas):
    /// Baby Leaf al productor ~45 % de la bolsa en góndola ($26.600–29.300/kg en 2026) con ~2,2 % de suba por mes;
    /// mantecosa hidropónica ~$1.700–2.600/kg con suba en otoño-invierno.
    /// </summary>
    internal static (decimal BabyLeaf, decimal Conventional) PriceModel(DateOnly date)
    {
        var months = (date.Year - 2024) * 12 + date.Month - 10;
        var babyLeaf = 8_500m * (decimal)Math.Pow(1.022, months);
        var seasonal = date.Month is 4 or 5 or 6 or 7 ? 1.3m : date.Month is 8 or 3 ? 1.1m : 1m;
        var conventional = 1_650m * (decimal)Math.Pow(1.01, months) * seasonal;
        return (Math.Round(babyLeaf / 50m) * 50m, Math.Round(conventional / 50m) * 50m);
    }

    private (decimal BabyLeaf, decimal Conventional) PricesOn(DateOnly date)
    {
        var row = _priceQuarters.LastOrDefault(q => q.From <= date);
        return row == default ? PriceModel(date) : (row.BabyLeaf, row.Conventional);
    }

    private readonly List<(DateOnly From, decimal BabyLeaf, decimal Conventional)> _priceQuarters = [];

    /// <summary>Precios cargados en Ajustes &gt; Parámetros cada trimestre (globales, origen manual).</summary>
    private void AddPriceHistory(DateOnly historyStart)
    {
        var start = new DateOnly(historyStart.Year, (historyStart.Month - 1) / 3 * 3 + 1, 1);
        for (var from = start; from <= _today; from = from.AddMonths(3))
        {
            var (bl, conv) = PriceModel(from.AddMonths(1));
            _priceQuarters.Add((from, bl, conv));
            var until = from.AddMonths(3) > _today ? (DateOnly?)null : from.AddMonths(3).AddDays(-1);
            foreach (var (destination, value) in new[] { (OptimizationContractDestination.BabyLeaf, bl), (OptimizationContractDestination.Conventional, conv) })
            {
                _data.Prices.Add(new CostPriceCatalog
                {
                    Id = ++_priceId,
                    Destination = destination,
                    Item = OptimizationContractCostItem.PricePerKg,
                    Value = value,
                    Currency = "ARS",
                    ValidFrom = from,
                    ValidUntil = until,
                    Source = CostPriceResolver.ManualSource,
                    CreatedAtUtc = ToUtc(from.ToDateTime(new TimeOnly(10, 0))),
                });
            }
        }

        _data.Settings[SystemParametersService.BabyLeafGramsKey] = _w.BabyLeafGramsPerPlant.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _data.Settings[SystemParametersService.ConventionalGramsKey] = _w.ConventionalGramsPerPlant.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _data.Settings[SystemParametersService.NutrientPricePerLiterKey] = NutrientPricePerLiter.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------
    // Cierre: clima, pronósticos, lotes en curso y estado de los nodos
    // ------------------------------------------------------------------

    private void Finish(DateOnly historyStart)
    {
        // Lotes en curso: su receta y su GDD de hoy quedan como los dejaría la web.
        foreach (var bench in _benches)
        {
            if (bench.Lot is { } run)
            {
                if (run.Recipe is { } recipe)
                    _data.Recipes.Add(recipe);
                run.Lot.AccumulatedGdd = Math.Round(run.Gdd, 2);
                run.Lot.CurrentPh = run.LastPh;
                run.Lot.CurrentEc = run.LastEc;
                _data.Story.Add($"{run.Lot.Name}: en curso, día {_today.DayNumber - run.Lot.SowingDate.DayNumber}, " +
                                $"GDD {run.Gdd:0}, receta pH {run.Recipe?.PhOffset:+0.00;-0.00;0} · CE {run.Recipe?.EcOffset:+0.00;-0.00;0}");
            }

            _data.Nodes.Add(new NodeFinalState(
                bench.Bench.NodeId,
                bench.Mode,
                bench.LastConnectionUtc,
                bench.LastAcceptedUtc,
                bench.LastRejectedUtc,
                bench.LastPollUtc,
                Firmware,
                bench.Pumps.Values.ToDictionary(p => p.Actuator.Id, p => (p.Effect, p.Samples)),
                new Dictionary<int, DateTime?>(bench.Calibrations),
                bench.Bench.CircuitId));
        }

        WeatherAndForecasts(historyStart);
    }

    /// <summary>Clima de afuera cada 3 horas (WeatherRecords) y el pronóstico diario guardado de cada día (DailyWeatherForecasts).</summary>
    private void WeatherAndForecasts(DateOnly historyStart)
    {
        double pressure = 1013;
        for (var date = historyStart; date <= _today.AddDays(7); date = date.AddDays(1))
        {
            var day = _climate[date];
            var max = day.Normal.OutdoorMax + day.Anomaly;
            var min = day.Normal.OutdoorMin + day.Anomaly * 0.8;
            var fetched = ToUtc(date.AddDays(-1).ToDateTime(new TimeOnly(3, 0)));
            if (fetched > _nowUtc)
                fetched = _nowUtc.AddMinutes(-30);
            _data.Forecasts.Add(new DailyWeatherForecast
            {
                Id = ++_forecastId,
                Date = date,
                TempMin = Math.Round((decimal)(min + HistoryPlanner.Gaussian(_rng) * 1.2), 1),
                TempMax = Math.Round((decimal)(max + HistoryPlanner.Gaussian(_rng) * 1.2), 1),
                FetchedAt = fetched,
            });

            if (date > _today)
                continue;
            var outside = new Model.GreenhouseClimate.InsideDay(max, min, day.Normal.OutdoorRh);
            for (var hour = 0; hour < 24; hour += 3)
            {
                var at = ToUtc(date.ToDateTime(new TimeOnly(hour, 0)));
                if (at > _nowUtc)
                    break;
                pressure = Math.Clamp(pressure + HistoryPlanner.Gaussian(_rng) * 1.5 - (day.Event == "lluvia" ? 1.2 : 0) + (1013 - pressure) * 0.05, 995, 1030);
                var temperature = Model.GreenhouseClimate.TemperatureAt(outside, outside, outside, hour);
                var humidity = Math.Clamp(Model.GreenhouseClimate.HumidityAt(outside with { BaseRh = day.Normal.OutdoorRh - 5 }, temperature)
                                          + (day.Event == "lluvia" ? 15 : 0), 20, 100);
                var clouds = day.Event == "lluvia" ? 85 + _rng.Next(15) : Math.Clamp((int)(humidity - 40 + HistoryPlanner.Gaussian(_rng) * 20), 0, 100);
                _data.Weather.Add(new WeatherRecord
                {
                    Id = ++_weatherId,
                    Timestamp = at,
                    Temp = Math.Round(temperature, 1),
                    FeelsLike = Math.Round(temperature + (temperature > 26 ? (humidity - 50) * 0.05 : -1.2), 1),
                    Humidity = Math.Round(humidity, 0),
                    Pressure = Math.Round(pressure, 0),
                    WindSpeed = Math.Round(Math.Abs(3 + HistoryPlanner.Gaussian(_rng) * 1.8), 1),
                    Clouds = clouds,
                    Visibility = day.Event == "lluvia" ? 4_000 : 10_000,
                    Description = day.Event == "lluvia" ? "lluvia ligera"
                        : clouds > 75 ? "muy nuboso" : clouds > 40 ? "nubes dispersas" : clouds > 15 ? "algo de nubes" : "cielo claro",
                });
            }
        }
    }
}
