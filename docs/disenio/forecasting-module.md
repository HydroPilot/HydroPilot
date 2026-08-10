# Módulo Forecasting — Documentación Técnica

## Estado: Fase 1 completa (2026-08)

Módulo funcional de punta a punta con datos mock realistas. Corresponde al Epic "Módulo Forecasting" del Sprint 1 (historias #4.1, #4.2, #4.4; #4.3 pendiente de notificaciones).

---

## Cómo funciona (concepto)

### Grados Día de Desarrollo (GDD)

Medida agronómica que acumula calor diario por encima de una temperatura base:

```
GDD_diario = max(0, (min(Tmax, 30) + Tmin) / 2 - Tbase)
```

- **Tbase** = 4.5 °C (lechuga, configurable en `CropType.BaseTemperature`)
- **Cap de 30 °C**: por encima la planta no acelera su desarrollo
- **GDD objetivo** = 300 °Cd para baby leaf (`CropType.GddTarget`)

Cuando `GDD_acumulado >= GDD_objetivo` → la planta está lista para cosechar.

### Fuentes de temperatura

| Período | Fuente | Mecanismo |
|---------|--------|-----------|
| Siembra → hoy | Sensor `Temperatura` del invernadero | Agrupa lecturas por día → Tmax/Tmin reales |
| Hoy → +7 días | `DailyWeatherForecasts` (OpenWeather) | DB primero; fetch perezoso si faltan fechas (máx. 1 llamada/día) |
| Fechas sin pronóstico | Fallback | Promedio de GDD de los últimos 7 días del sensor |

### Fecha de cosecha estimada

Suma día a día la proyección futura hasta alcanzar el target. Si el horizonte de 7 días no alcanza, extrapola con el último valor diario. Sin datos, usa `EstimatedDaysToHarvest` del cultivo.

---

## Arquitectura de archivos

### Modelos (`Models/`)
| Clase | Tabla | Rol |
|-------|-------|-----|
| `CropType` | `CropTypes` | Cultivo: GDD target, Tbase, pH/EC óptimos, rendimiento base por m² |
| `LotStatus` | `LotStatuses` | Catálogo: ACTIVO, COSECHADO, DESCARTADO, EN_PAUSA |
| `Lot` | `Lots` | Lote: siembra, área, **rendimiento real (kg)** y **fecha de cosecha real** |
| `Prediction` | `Predictions` | Historial de predicciones (fecha estimada, GDD, rendimiento, versión del modelo) |
| `DailyWeatherForecast` | `DailyWeatherForecasts` | Pronóstico diario (Tmin/Tmax por fecha, con FetchedAt) |
| `AppSetting` | `AppSettings` | Settings key-value (`WeatherFetchEnabled`, `HarvestAlertDays`) |

### Servicios (`Services/`)
| Servicio | Responsabilidad |
|----------|-----------------|
| `GddService` | GDD histórico (sensores), proyección futura (DB→lazy fetch→fallback), estimación de fecha de cosecha |
| `YieldService` | Rendimiento estimado: historial real si hay ≥2 ciclos, si no constante ±15% |
| `WeatherService` | Fetch de clima actual + pronóstico, fetch perezoso por rango de fechas, guard 1 vez/día |
| `WeatherFetcherHostedService` | Corre a las 06:00 UTC, respeta toggle `WeatherFetchEnabled`, guard por `FetchedAt` |
| `SettingsService` | Lectura/escritura de AppSettings |

### API (`Controllers/ForecastingController.cs`)
| Endpoint | Auth | Descripción |
|----------|------|-------------|
| `GET /api/forecasting?lotId=` | API key **o** sesión | Forecast completo + persiste Prediction |
| `GET /api/forecasting/lots` | Sesión | Lista lotes para dropdown |
| `POST /api/forecasting/lots` | API key | Crea lote (scripts) |
| `POST /api/forecasting/lots/{id}/harvest` | API key | Registra cosecha real (kg + fecha) y marca COSECHADO |

### UI (`Forecasting.razor`)
- Dropdown de lote + botón "+ Nuevo lote" (formulario inline)
- Toggle "Sincronización climática diaria" (solo admin)
- Metric cards: rendimiento estimado (+precisión MAPE si hay historial), fecha de cosecha (+badge "⚠ Cosecha inminente"), GDD acumulado vs objetivo
- Gráfico: GDD acumulado histórico (sólido, con ejes + tooltips por punto) + proyección 7 días (punteado overlay)
- Escenarios de rendimiento con % reales (σ del historial o ±15% fijo)
- Disclaimer: "Valores estimados de soporte a la decisión"
- `SimpleLineChart` extendido: ejes con labels, gridlines, target line, tooltips nativos, `RotateXLabels`, `HideLabels` (overlay)

---

## Precisión del modelo (MAPE)

Al cerrar un lote (`POST harvest`), se compara la última predicción contra el resultado real:

- **MAPE rendimiento** = |estimado − real| / real × 100 (promedio de ciclos cerrados)
- **Error de días** = |fecha estimada − fecha real| en días (solo predicciones generadas **antes** de la cosecha real)

Se muestra en la metric card: "Precisión: MAPE X% · ±Y días (N ciclos)".

## Confianza del rendimiento
- **Con historial** (≥2 ciclos COSECHADO del mismo cultivo): baseline = promedio kg/m² × área, escenarios ±1σ, confianza = 60 + 8×N (máx 95)
- **Sin historial**: constante del cultivo × área, ±15% fijo, confianza heurística por avance del ciclo

---

## Datos mock (`/home/nix/utn/proyecto/scripts/mock_historical_data.py`)

Genera TODO el ecosistema de prueba:

1. **N ciclos históricos COSECHADO** (default 3, 24 días c/u):
   - Crea lote → simula lecturas horarias realistas (temp sinusoidal, humedad inversa, pH/EC gaussianos)
   - Genera la **predicción** del ciclo (GET forecast con API key)
   - Calcula **rendimiento realista** correlacionado con la temp media del ciclo (`realistic_yield`: 2.0-4.2 kg/m², óptimo 18-22 °C)
   - Registra la **cosecha real** (kg + fecha)
2. **1 ciclo actual ACTIVO** (15 días transcurridos) con lecturas en curso

```bash
python3 scripts/mock_historical_data.py --url https://localhost:7059
# Args: --cycles N (default 3), --current-cycle-days N (15), --cycle-length N (24)
```

El resultado: el módulo funciona completo — gráfico con proyección, escenarios basados en historial real (13.35 kg base en la demo), MAPE calculado (2.9% en la demo).

---

## Clima (OpenWeather)

| Aspecto | Comportamiento |
|---------|----------------|
| Fetch programado | 1 vez/día a las 06:00 UTC (hosted service) |
| Toggle | `WeatherFetchEnabled` en AppSettings, OFF por defecto, solo admin lo cambia (UI forecasting) |
| Fetch perezoso | `GddService` consulta DB primero; si faltan fechas y hoy no se consultó la API → 1 llamada, guarda la ventana |
| Sin API key | Todo degrada a fallback (promedio del sensor) |
| Config | `Weather:ApiKey`, `Weather:Lat`, `Weather:Lon` (user secrets / Azure App Settings) |

---

## Migraciones (aditivas, compatibles con Azure)

| Migración | Contenido |
|-----------|-----------|
| `AddForecasting` | CropTypes, LotStatuses, Lots, Predictions + FK SensorReadings→Lots |
| `AddWeatherForecast` | DailyWeatherForecasts, AppSettings |
| `AddLotHarvestData` | Lots.ActualYieldKg, Lots.ActualHarvestDate |

---

## Lo que resta

| Pendiente | Notas |
|-----------|-------|
| **Alertas de cosecha por email/push** (historia #4.3) | Badge en UI ya existe; el pipeline de notificaciones se hace con el módulo de Alertas (Sprint 4) |
| **Datos reales** | Hoy todo es mock; con operación real el sistema aprende solo (cada cosecha cierra el ciclo de precisión) |
| **API de 50 años de OpenWeather** | Diferida (acordado): permitiría matching de temporadas históricas para yield |
| **Verificación del fetch climático con API key real** | El guard 1×/día y el lazy fetch están implementados pero no probados contra OpenWeather real desde dev |
| **Deploy Azure** | La rama `forecasting` aún no se mergeó a `dev`/`main` |
| **Etapa fenológica / plantas individuales** | El DER las contempla; la predicción es a nivel lote por ahora |

## Cómo probar en local

```bash
dotnet run
# en otra terminal:
python3 scripts/mock_historical_data.py --url https://localhost:7059
# abrir https://localhost:7059/forecasting (login con admin)
```

## Branches / commits

- Rama `forecasting` (desde `dev`)
- Commits: `inicio forecasting`, `feat: forecasting - proyeccion en grafico, historial de rendimiento, precision del modelo, alta de lotes, alerta minima`, `fix: auth GET forecast con api key o sesion, precision de dias solo con predicciones pre-cosecha`
- Sin merge a `dev`/`main` aún (pendiente de revisión)
