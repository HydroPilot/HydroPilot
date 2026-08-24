namespace HydroPilotWeb.Controllers;

/// <summary>Listado de lote para dropdowns (usado por Forecasting y Lotes).</summary>
public record LotSummary(
    int Id,
    string CropType,
    string Status,
    DateOnly SowingDate,
    decimal AreaM2
);

/// <summary>Creación de lote vía API (script mock / integración).</summary>
public record CreateLotRequest(
    string CropTypeName,
    string Status,
    DateOnly SowingDate,
    decimal PlantedAreaM2,
    int? GreenhouseId = null
);

/// <summary>Registro de cosecha real (idempotente, F-04).</summary>
public record RecordHarvestRequest(
    decimal ActualYieldKg,
    DateOnly? ActualHarvestDate
);