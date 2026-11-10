using System.Globalization;

namespace HydroPilotWeb.Services.SimulationEnvironment.Dataset;

/// <summary>
/// El dataset de la demo (notas/demo/dataset-demo.xlsx): el guion de lo que pasó en el invernadero. El importador
/// genera el historial con la lógica de siempre, pero respetando estas fechas y cantidades.
/// </summary>
public sealed record DemoDataset(
    DemoGeneral General,
    IReadOnlyList<DemoCircuit> Circuits,
    IReadOnlyList<DemoLot> Lots,
    IReadOnlyList<DemoMap> Maps,
    IReadOnlyList<DemoPrice> Prices,
    DemoClimate Climate,
    IReadOnlyList<DemoUser> Users)
{
    /// <summary>Todas las fechas corridas <paramref name="days"/> días (para cargarlo otro día y que los lotes en curso lleguen igual).</summary>
    public DemoDataset Shifted(int days)
    {
        if (days == 0)
            return this;
        DateOnly Move(DateOnly date) => date.AddDays(days);
        DateOnly? MoveOptional(DateOnly? date) => date?.AddDays(days);
        DemoEvent? MoveEvent(DemoEvent? e) => e is null ? null : e with { Date = Move(e.Date) };
        return this with
        {
            General = General with
            {
                Today = Move(General.Today),
                AmbientCalibration = MoveOptional(General.AmbientCalibration),
            },
            Circuits = Circuits.Select(c => c with { PhCalibration = MoveOptional(c.PhCalibration), EcCalibration = MoveOptional(c.EcCalibration) }).ToList(),
            Lots = Lots.Select(l => l with
            {
                Sowing = Move(l.Sowing),
                Transplant = MoveEvent(l.Transplant),
                Cut1 = MoveEvent(l.Cut1),
                Cut2 = MoveEvent(l.Cut2),
                AdultCut = MoveEvent(l.AdultCut),
                DenseDiscard = MoveEvent(l.DenseDiscard),
                AdultDiscard = MoveEvent(l.AdultDiscard),
            }).ToList(),
            Prices = Prices.Select(p => p with { From = Move(p.From) }).ToList(),
            Climate = Climate with { Events = Climate.Events.Select(e => e with { From = Move(e.From), To = Move(e.To) }).ToList() },
        };
    }

    /// <summary>
    /// Cada lote con su fecha de siembra en el nombre ("Lote B26 · 08/10/2026"): se reemplaza lo que haya después del
    /// primer " · " (o se agrega). Se aplica después de correr las fechas, así el nombre siempre coincide con la siembra.
    /// Los mapas siguen a su lote.
    /// </summary>
    public DemoDataset WithSowingInNames()
    {
        static string Base(string name)
        {
            var cut = name.IndexOf(" · ", StringComparison.Ordinal);
            return (cut < 0 ? name : name[..cut]).Trim();
        }

        var renamed = Lots
            .Select(l => (Old: l.Name, Lot: l with { Name = $"{Base(l.Name)} · {l.Sowing.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}" }))
            .ToList();
        var byOldName = renamed
            .GroupBy(r => r.Old, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().Lot.Name, StringComparer.OrdinalIgnoreCase);
        return this with
        {
            Lots = renamed.Select(r => r.Lot).ToList(),
            Maps = Maps.Select(m => byOldName.TryGetValue(m.Lot, out var name) ? m with { Lot = name } : m).ToList(),
        };
    }
}

/// <summary>Hoja General: la fecha de la demo y los parámetros (Ajustes &gt; Parámetros).</summary>
public sealed record DemoGeneral(
    DateOnly Today,
    bool ShiftDates,
    string? GreenhouseName,
    string Currency,
    decimal BabyLeafGrams,
    decimal AdultGrams,
    decimal SolutionAPerLiter,
    decimal SolutionBPerLiter,
    decimal DecisionGdd,
    int StressDays,
    decimal MaxSizeFactor,
    decimal DenseSpacingCm,
    decimal AdultSpacingCm,
    int CleaningDays,
    IReadOnlyDictionary<string, int> SensorCheckDays,
    DateOnly? AmbientCalibration,
    decimal BabyLeafGddMin = 250m,
    decimal BabyLeafGddMax = 450m,
    decimal AdultCutGdd = 650m,
    decimal CycleEndGdd = 750m);

/// <summary>Hoja Circuitos.</summary>
public sealed record DemoCircuit(
    int Row,
    string Name,
    string Kind,
    int Rows,
    int Columns,
    decimal Liters,
    decimal SourceWaterEc,
    string? ControlMode,
    DateOnly? PhCalibration,
    DateOnly? EcCalibration)
{
    public int Holes => Rows * Columns;
}

/// <summary>Un movimiento del lote: el día y cuántas plantas.</summary>
public sealed record DemoEvent(DateOnly Date, int Plants);

/// <summary>Una fila de la hoja Lotes.</summary>
public sealed record DemoLot(
    int Row,
    string Name,
    string Circuit,
    DateOnly Sowing,
    int? Plants,
    decimal? BabyLeafGoal,
    decimal? ActiveGoal,
    DemoEvent? Transplant,
    IReadOnlyList<string> AdultCircuits,
    DemoEvent? Cut1,
    DemoEvent? Cut2,
    DemoEvent? AdultCut,
    DemoEvent? DenseDiscard,
    string? DenseReason,
    DemoEvent? AdultDiscard,
    string? AdultReason,
    string? Incident,
    string? Comment = null)
{
    public int Transplanted => Transplant?.Plants ?? 0;
    public int CutAsBabyLeaf => (Cut1?.Plants ?? 0) + (Cut2?.Plants ?? 0);
    public int AdultsCut => AdultCut?.Plants ?? 0;
    public int DenseDiscarded => DenseDiscard?.Plants ?? 0;
    public int AdultsDiscarded => AdultDiscard?.Plants ?? 0;

    /// <summary>El día en que sale su última planta del denso (null: todavía tiene plantas ahí).</summary>
    public DateOnly? LeavesDense(int plants) =>
        plants - Transplanted - CutAsBabyLeaf - DenseDiscarded > 0
            ? null
            : new[] { Transplant?.Date, Cut1?.Date, Cut2?.Date, DenseDiscard?.Date }.Max();

    /// <summary>El día en que salen sus últimas adultas (null: no trasplantó o le quedan adultas).</summary>
    public DateOnly? LeavesAdults() =>
        Transplanted == 0 || Transplanted - AdultsCut - AdultsDiscarded > 0
            ? null
            : new[] { AdultCut?.Date, AdultDiscard?.Date }.Max();
}

/// <summary>Códigos de la hoja Mapas: el estado de cada planta en su hueco del denso.</summary>
public static class DemoMapCodes
{
    /// <summary>Activa en el denso (antes "ED", "En desarrollo"; el lector acepta los dos).</summary>
    public const string Active = "ACT";
    public const string OldActive = "ED";
    public const string Candidate = "CAN";
    public const string BabyLeaf = "BL";
    public const string Adult = "AD";
    public const string CutBabyLeaf = "C-BL";
    public const string CutAdult = "C-AD";
    public const string DiscardedDense = "X";
    public const string DiscardedAdult = "X-AD";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Active, Candidate, BabyLeaf, Adult, CutBabyLeaf, CutAdult, DiscardedDense, DiscardedAdult,
    };

    /// <summary>Activas en el denso hoy.</summary>
    public static readonly IReadOnlySet<string> ActiveDense = new HashSet<string> { Active, Candidate, BabyLeaf };

    /// <summary>Trasplantadas (estén activas, cortadas o descartadas).</summary>
    public static readonly IReadOnlySet<string> Transplanted = new HashSet<string> { Adult, CutAdult, DiscardedAdult };
}

/// <summary>Un mapa de la hoja Mapas: código por hueco (fila, columna) del circuito denso.</summary>
public sealed record DemoMap(int Row, string Lot, IReadOnlyDictionary<(int Row, int Column), string> Codes)
{
    public int Count(string code) => Codes.Values.Count(c => c == code);

    public int Count(IReadOnlySet<string> codes) => Codes.Values.Count(codes.Contains);
}

public sealed record DemoPrice(int Row, DateOnly From, decimal BabyLeafPerKg, decimal AdultPerKg);

/// <summary>Hoja Clima: normales de afuera por mes (índice 0 = enero) y las olas de calor y frentes fríos.</summary>
public sealed record DemoClimate(IReadOnlyList<double> OutdoorMax, IReadOnlyList<double> OutdoorMin, IReadOnlyList<DemoClimateEvent> Events)
{
    /// <summary>Las normales del entorno de simulación (SMN 1991–2020).</summary>
    public static DemoClimate Default => new(
        [30.1, 28.9, 26.9, 23.2, 19.4, 16.4, 15.5, 17.9, 19.7, 22.6, 26.0, 29.0],
        [20.2, 19.4, 17.7, 14.1, 11.1, 8.4, 7.5, 8.9, 10.6, 13.4, 16.1, 18.6],
        []);
}

public sealed record DemoClimateEvent(DateOnly From, DateOnly To, string Kind, double Delta);

public sealed record DemoUser(int Row, string GivenName, string Surname, string Email, string Role, bool ReceivesEmail, string MinSeverity);
