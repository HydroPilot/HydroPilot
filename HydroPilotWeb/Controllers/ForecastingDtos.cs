namespace HydroPilotWeb.Controllers;

public record ForecastingResponse(
    int LotId,
    string CropType,
    string? Status,
    DateOnly SowingDate,
    decimal AreaM2,
    decimal GddAccumulated,
    decimal GddTarget,
    decimal GddDailyAverage,
    DateOnly? EstimatedHarvestDate,
    int DaysRemaining,
    decimal YieldConservative,
    decimal YieldBase,
    decimal YieldOptimistic,
    int ConfidencePercent,
    decimal? AccuracyMape,
    decimal? AccuracyDaysError,
    int AccuracyCycles,
    List<DailyGddPoint> GddHistory
);

public record DailyGddPoint(
    DateOnly Date,
    decimal Gdd
);
