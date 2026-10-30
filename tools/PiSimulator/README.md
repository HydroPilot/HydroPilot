# Pi virtual

Simula la Raspberry Pi del invernadero para probar el control de pH y CE sin hardware. Una sola Pi maneja todos
los circuitos del invernadero (densos y de Adultas): cada uno con su tanque, sus sondas de pH y CE y sus 5 bombas;
temperatura y humedad son del invernadero. Habla con la web por HTTP igual que la Pi real:

- al arrancar pide sus circuitos por `GET /api/nodos/{nodeId}/circuitos` (y otra vez cada minuto);
- manda lecturas por `POST /api/telemetria/lecturas`;
- pide sus comandos por `GET /api/nodos/{nodeId}/comandos`;
- responde cada uno por `POST /api/nodos/{nodeId}/comandos/{commandId}/resultado`.

Lleva una mesada virtual por circuito con el mismo modelo que el historial del entorno de simulación (los archivos de
`HydroPilotWeb/Services/SimulationEnvironment/Model` se compilan acá):
- tanque con las plantas activas del lote que tiene el circuito (las dice la web): toman agua y nutrientes según su
  edad y el clima (con calor la CE sube, con frío baja) y el nitrato que absorben sube el pH. Un circuito sin lote
  no consume;
- clima del invernadero en Buenos Aires (normales del SMN), con su ciclo diario;
- cada dosis se mezcla en unos 2 minutos, así se ve en la vista **Optimización** cómo cambia la solución;
- un flotante repone agua de red cuando el nivel baja del 85 %;
- la química del tanque es distinta a propósito de la de fábrica que supone la web: la web la aprende calibrando.

Con `--acelerar 60` las plantas y el clima corren una hora por minuto: se ven en minutos las derivas y
correcciones de horas. La mezcla, las dosis y la conversación con la web siguen en tiempo real.

## Uso

Con la web corriendo (`dotnet run` en `HydroPilotWeb`):

```
dotnet run --project tools/PiSimulator -- --acelerar 60
dotnet run --project tools/PiSimulator -- --escenario calor --acelerar 60
dotnet run --project tools/PiSimulator -- --circuitos 1 --dias 8
```

**Circuitos (desde 2026-10-26):** sin `--circuitos`, la Pi pide a la web su configuración
(`GET /api/nodos/{nodeId}/circuitos`, con la misma API key): por circuito, nombre, tipo, litros, claves de las sondas
y de las bombas, plantas activas del lote y días desde la siembra; y las claves de temperatura y humedad. Arma un
tanque por circuito con sondas de pH y CE y lo muestra al arrancar. Cada minuto la vuelve a pedir: si cambian las
plantas (corte, descarte, trasplante) o entra un lote nuevo, el tanque se actualiza solo. Con el entorno de simulación
(`--launch-profile simulacion`, ver `notas/simulacion/entorno-simulacion.md`) son 7: dos densos con su lote en curso y
cinco de Adultas.

Si la web no responde (o con IoT Hub, que no tiene ese pedido), usa 2 circuitos densos con las claves base.
`--circuitos n` fuerza ese modo local: n circuitos con `--dias` y `--plantas`.

**Claves de cada circuito** (las mismas que muestra Ajustes > Circuitos): las que dice la web. En el modo local, el
circuito 1 usa `ph-solucion`, `ec-solucion`, `ph-up`, `ph-down`, `nutrient` (Solución A), `nutrient-b` (Solución B) y `water`; el n, las mismas con `-n`
(`ph-solucion-2`, `ph-up-2`). Temperatura y humedad van una sola vez (`temp-ambiente`, `hum-ambiente`).

**API key:** la toma de `TelemetryApiKey` en los user-secrets de HydroPilotWeb. También se puede pasar con
`--api-key` o con la variable `HYDROPILOT_API_KEY`.

| Opción | Default |
|---|---|
| `--url` | `https://localhost:7059` |
| `--nodo` (la Pi del invernadero) | `rpi-inv-01` |
| `--circuitos` (modo local, sin pedirlos a la web) | los de la web |
| `--escenario` | `normal` |
| `--acelerar` (tiempo de plantas y clima) | 1 (tiempo real) |
| `--dias` (con `--circuitos`: días desde la siembra de cada uno, separados por `/`) | `12/4` |
| `--plantas` (con `--circuitos`: de cada mesada) | 200 |
| `--lecturas` (s entre lecturas) | 15 |
| `--consulta` (s entre consultas de comandos) | 5 |
| `--litros` (de cada tanque) | 500 |
| `--ph` / `--ce` (valores iniciales) | 6,0 / 1,5 |
| `--inseguro` (no valida el certificado HTTPS) | — |
| `--transporte` | `http`. Con `iothub` usa Azure IoT Hub por MQTT |
| `--conexion` | cadena del dispositivo en IoT Hub (o `HYDROPILOT_DEVICE_CONNECTION`); el nodo es su DeviceId |

**Por IoT Hub** (fase 3, guía en `notas/control/azure-iot-hub.md`):

```
dotnet run --project tools/PiSimulator -- --transporte iothub --conexion "HostName=…;DeviceId=rpi-inv-01;SharedAccessKey=…"
```

- Las lecturas y los resultados viajan como mensajes D2C (`type=telemetry` / `type=command-result`).
- Los comandos llegan como C2D: se completan después de ejecutarlos.

`calor` y `sin-conexion` afectan a toda la Pi; los demás, solo al primer circuito de la lista.

| Escenario | Qué pasa |
|---|---|
| `normal` | consumo de las plantas según edad y clima |
| `calor` | ola de calor (+7 °C): toman mucha agua, la CE sube y el motor agrega agua |
| `ph-sube` | el pH sube 1 punto por hora |
| `ce-baja` | la CE baja 0,6 por hora |
| `bomba-rota` | las bombas de pH responden EXECUTED pero no cambian nada |
| `sin-conexion` | del minuto 1 al 11 no manda lecturas ni pide comandos (las dosis vencen) |
| `sensor-pegado` | la sonda de pH marca siempre 6,40 |

**Teclas** (las de tanque van al circuito elegido, el 1 al arrancar):
- `1`…`9`: elige el circuito;
- `p` / `b`: pH +0,5 / −0,5;
- `c` / `n`: CE −0,3 / +0,3 (sobredosis de nutrientes: el motor diluye con agua);
- `r`: repone agua hasta llenar el tanque;
- `a`: cambia la aceleración de todos (×1, ×10, ×60, ×240);
- `x`: corta o reanuda la conexión;
- `q`: sale.

## Qué valida antes de prender una bomba

Igual que la Pi real:
- que el comando no esté vencido (`expiresAt`);
- que la bomba exista (la clave dice el circuito: `ph-up-2` es la del 2) y la unidad coincida;
- que no supere su propio límite por dosis (el doble del de la web: doble control).

Si algo falla, responde `REJECTED` con el motivo. Nunca ejecuta dos veces el mismo `commandId`: si lo
recibe de nuevo, solo vuelve a informar el resultado.

## Contrato

**Comando** (respuesta de `GET /api/nodos/{nodeId}/comandos`):

```json
{
  "nodeId": "rpi-inv-01",
  "serverTimeUtc": "2026-10-16T13:00:10Z",
  "commands": [{
    "type": "dose-command",
    "commandId": "6f1c2a9e-…",
    "nodeId": "rpi-inv-01",
    "actuator": "ph-down",
    "amount": 20,
    "unit": "ml",
    "createdAt": "2026-10-16T13:00:05Z",
    "expiresAt": "2026-10-16T13:05:05Z",
    "source": "manual",
    "reason": { "parameter": "pH", "value": 6.82, "target": 6.0, "readingId": "pv-ph-20261016130000123" }
  }]
}
```

- Las bombas son `ph-up`, `ph-down`, `nutrient` (Solución A) y `nutrient-b` (Solución B, desde 2026-10-28) en ml, y `water` (L); las del circuito n llevan `-n` (`water-2`).
- La nube manda la cantidad; la Pi la convierte a segundos de bomba con su calibración.

**Resultado** (`POST /api/nodos/{nodeId}/comandos/{commandId}/resultado`):

```json
{ "status": "EXECUTED", "executedAmount": 20, "startedAt": "…", "finishedAt": "…", "error": null }
```

- `status` puede ser `EXECUTED`, `REJECTED` (con `error`) o `FAILED` (con lo que llegó a aplicar).
- Repetir el mismo resultado devuelve 200. Uno distinto sobre un comando ya cerrado devuelve 409.
