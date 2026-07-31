namespace HydroPilotWeb.Controllers;

// DTOs para la API de telemetría

public record TelemetryRequest(
    string NodoId,
    DateTime Timestamp,
    List<ReadingDto> Lecturas
);

public record ReadingDto(
    string SensorRef,
    decimal Valor
);

public record TelemetryResponse(
    int Insertadas,
    int Alertas,
    DateTime Timestamp
);

public record ReadingQueryResponse(
    int Id,
    int SensorId,
    string SensorName,
    string SensorType,
    decimal Value,
    string? Unit,
    DateTime Timestamp,
    DateTime CreatedAt
);
