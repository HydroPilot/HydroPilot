using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Lotes;

namespace HydroPilotWeb.Services.SimulationEnvironment.Dataset;

/// <summary>
/// Después de generar: compara el historial con el Excel lote por lote (plantas por destino y fecha, el estado de cada
/// hueco del mapa, que las cortadas como Baby Leaf lo fueran y el GDD medido de cada movimiento contra su ventana). Lo
/// que no coincide vuelve como aviso.
/// </summary>
public static class DemoDatasetVerifier
{
    public static IReadOnlyList<string> Verify(DemoScript script, HistoryData data, IReadOnlyDictionary<string, int> commercialStageIds)
    {
        var issues = new List<string>();
        var g = script.General;
        var stageNames = commercialStageIds.ToDictionary(kv => kv.Value, kv => kv.Key);
        foreach (var lotScript in script.Lots)
        {
            var lot = lotScript.Lot;
            var generated = data.Lots.FirstOrDefault(l => l.Name == lot.Name);
            if (generated is null)
            {
                issues.Add($"{lot.Name}: no se generó.");
                continue;
            }
            var plants = data.Plants.Where(p => p.LotId == generated.Id).ToList();
            void Same(string what, int actual, int expected)
            {
                if (actual != expected)
                    issues.Add($"{lot.Name}: {what}, {actual} generadas y {expected} en el Excel.");
            }
            bool On(DateOnly? date, DemoEvent? e) => e is not null && date == e.Date;
            Same("plantas", plants.Count, lotScript.Plants);
            Same("trasplantadas", plants.Count(p => On(p.TransplantedOn, lot.Transplant)), lot.Transplanted);
            Same("1er corte Baby Leaf", plants.Count(p => p.TransplantedOn is null && p.OperationalState == PlantOperationalState.Cosechada && On(p.HarvestDate, lot.Cut1)), lot.Cut1?.Plants ?? 0);
            Same("2do corte Baby Leaf", plants.Count(p => p.TransplantedOn is null && p.OperationalState == PlantOperationalState.Cosechada && On(p.HarvestDate, lot.Cut2)), lot.Cut2?.Plants ?? 0);
            Same("corte de adultas", plants.Count(p => p.TransplantedOn is not null && p.OperationalState == PlantOperationalState.Cosechada && On(p.HarvestDate, lot.AdultCut)), lot.AdultsCut);
            Same("descartadas en el denso", plants.Count(p => p.TransplantedOn is null && p.OperationalState == PlantOperationalState.Descartada && On(p.DiscardDate, lot.DenseDiscard)), lot.DenseDiscarded);
            Same("descartadas en Adultas", plants.Count(p => p.TransplantedOn is not null && p.OperationalState == PlantOperationalState.Descartada && On(p.DiscardDate, lot.AdultDiscard)), lot.AdultsDiscarded);
            Same("activas hoy", plants.Count(p => p.OperationalState == PlantOperationalState.Activa),
                lotScript.Plants - lot.CutAsBabyLeaf - lot.DenseDiscarded - lot.AdultsCut - lot.AdultsDiscarded);

            if (lotScript.Map is { } map)
            {
                var wrong = new List<string>();
                foreach (var plant in plants)
                {
                    var hole = (plant.DenseRow ?? plant.Row, plant.DenseColumn ?? plant.Column);
                    var expected = map.Codes.GetValueOrDefault(hole);
                    var actual = Code(plant, stageNames);
                    if (expected != actual)
                        wrong.Add($"F{hole.Item1}-C{hole.Item2}: {expected ?? "vacío"} en el Excel, {actual} generada");
                }
                if (wrong.Count > 0)
                    issues.Add($"{lot.Name}: {wrong.Count} huecos del mapa no quedaron como en el Excel ({string.Join("; ", wrong.Take(3))}{(wrong.Count > 3 ? "; …" : "")}).");
            }

            foreach (var e in data.ScriptedEvents.Where(e => e.LotId == generated.Id))
            {
                if (e.NotReady > 0)
                    issues.Add($"{lot.Name}: en el {e.Event} del {e.Date:dd/MM/yyyy}, {e.NotReady} de {e.Plants} no estaban como " +
                               $"{(e.Event.Contains("Baby Leaf") ? "Baby Leaf" : "Adulta")} en su evaluación.");
                var (min, max) = e.Event switch
                {
                    "Trasplante" => (g.DecisionGdd, g.BabyLeafGddMax),
                    "1er corte Baby Leaf" or "2do corte Baby Leaf" => (g.BabyLeafGddMin, g.BabyLeafGddMax),
                    "Corte de adultas" => (g.AdultCutGdd, g.CycleEndGdd),
                    _ => (decimal.MinValue, decimal.MaxValue),
                };
                if (e.Gdd < min || e.Gdd > max)
                    issues.Add($"{lot.Name}: el {e.Event} del {e.Date:dd/MM/yyyy} quedó a los {e.Gdd:0} GDD medidos (va de {min:0} a {max:0}; el Excel lo estima con el clima normal).");
            }
        }
        return issues;
    }

    /// <summary>El código del mapa que le corresponde a una planta generada.</summary>
    private static string Code(Plant plant, IReadOnlyDictionary<int, string> stageNames)
    {
        var transplanted = plant.TransplantedOn is not null;
        return plant.OperationalState switch
        {
            PlantOperationalState.Cosechada => transplanted ? DemoMapCodes.CutAdult : DemoMapCodes.CutBabyLeaf,
            PlantOperationalState.Descartada => transplanted ? DemoMapCodes.DiscardedAdult : DemoMapCodes.DiscardedDense,
            _ when transplanted => DemoMapCodes.Adult,
            _ => stageNames.GetValueOrDefault(plant.CommercialStageId ?? 0) switch
            {
                CommercialStageNames.BabyLeafApta => DemoMapCodes.BabyLeaf,
                CommercialStageNames.CandidataBabyLeaf => DemoMapCodes.Candidate,
                CommercialStageNames.Activa => DemoMapCodes.Active,
                { } other => other,
                null => "sin estado",
            },
        };
    }
}
