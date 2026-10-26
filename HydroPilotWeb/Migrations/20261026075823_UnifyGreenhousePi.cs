using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <summary>
    /// Una Pi por invernadero (2026-10-26): AddCircuits convirtió cada nodo en un circuito, así que las bases con dos
    /// nodos quedaron con dos Pis. Acá todo pasa a la Pi que queda en cada invernadero (rpi-inv-01, o la más vieja):
    /// - los circuitos de las otras Pis, con sus sondas y bombas, toman las posiciones siguientes y sus claves llevan el
    ///   sufijo de la posición ("ph-solucion-2", "ph-up-2"), como los crea CircuitSetup;
    /// - los demás sensores conservan su clave salvo que choque (entonces llevan "~Id");
    /// - lecturas, batches, rechazos, dosis, errores de sensor y anomalías pasan a la Pi que queda (las lecturas y los
    ///   batches cuyo id se repite en otra Pi llevan el sufijo "-n{nodo viejo}");
    /// - las alertas abiertas de "nodo desconectado" de las Pis que se van se resuelven, y esas Pis se borran.
    /// Solo cambia datos; el Down no los separa de nuevo.
    /// </summary>
    public partial class UnifyGreenhousePi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
SET NOCOUNT ON;

-- Pi que queda en cada invernadero: rpi-inv-01 si está ahí, si no la más vieja.
DECLARE @merge TABLE (OldId int PRIMARY KEY, KeeperId int NOT NULL);
WITH ranked AS (
    SELECT Id, GreenhouseId,
           ROW_NUMBER() OVER (PARTITION BY GreenhouseId
                              ORDER BY CASE WHEN Identifier = N'rpi-inv-01' THEN 0 ELSE 1 END, Id) AS rn
    FROM IotNodes)
INSERT INTO @merge (OldId, KeeperId)
SELECT r.Id, k.Id
FROM ranked r
JOIN ranked k ON k.GreenhouseId = r.GreenhouseId AND k.rn = 1
WHERE r.rn > 1;

IF EXISTS (SELECT 1 FROM @merge)
BEGIN
    -- Posición de cada circuito que llega: después de la última que usa la Pi que queda (por cantidad de circuitos
    -- y por el sufijo de las claves de sus bombas). La posición 1 no lleva sufijo.
    DECLARE @moved TABLE (CircuitId int PRIMARY KEY, KeeperId int NOT NULL, Suffix nvarchar(12) NOT NULL);
    WITH used AS (
        SELECT k.KeeperId,
               (SELECT COUNT(*) FROM Circuits c WHERE c.NodeId = k.KeeperId) AS Circuits,
               ISNULL((SELECT MAX(CASE WHEN a.TechnicalKey = a.Kind THEN 1
                                       ELSE ISNULL(TRY_CAST(SUBSTRING(a.TechnicalKey, LEN(a.Kind) + 2, 10) AS int), 0) END)
                       FROM Actuators a
                       WHERE a.NodeId = k.KeeperId AND (a.TechnicalKey = a.Kind OR a.TechnicalKey LIKE a.Kind + N'-%')), 0) AS MaxPos
        FROM (SELECT DISTINCT KeeperId FROM @merge) k),
    positions AS (
        SELECT c.Id AS CircuitId, m.KeeperId,
               CASE WHEN u.MaxPos > u.Circuits THEN u.MaxPos ELSE u.Circuits END
               + ROW_NUMBER() OVER (PARTITION BY m.KeeperId ORDER BY c.NodeId, c.Id) AS Position
        FROM Circuits c
        JOIN @merge m ON m.OldId = c.NodeId
        JOIN used u ON u.KeeperId = m.KeeperId)
    INSERT INTO @moved (CircuitId, KeeperId, Suffix)
    SELECT CircuitId, KeeperId, CASE WHEN Position <= 1 THEN N'' ELSE N'-' + CAST(Position AS nvarchar(10)) END
    FROM positions;

    -- Sondas de pH y CE de esos circuitos: la clave base con el sufijo (la activa más vieja de cada tipo); otras
    -- sondas del circuito llevan su Id para no chocar.
    WITH probes AS (
        SELECT s.Id, mv.Suffix, t.Name AS TypeName,
               ROW_NUMBER() OVER (PARTITION BY s.CircuitId, s.SensorTypeId
                                  ORDER BY CASE WHEN s.IsActive = 1 THEN 0 ELSE 1 END, s.Id) AS rn
        FROM Sensors s
        JOIN @moved mv ON mv.CircuitId = s.CircuitId
        JOIN SensorTypes t ON t.Id = s.SensorTypeId)
    UPDATE s
    SET TechnicalKey = CASE
            WHEN p.rn = 1 AND p.TypeName = N'pH' THEN N'ph-solucion' + p.Suffix
            WHEN p.rn = 1 AND p.TypeName = N'CE' THEN N'ec-solucion' + p.Suffix
            ELSE LEFT(s.TechnicalKey, 80) + N'~' + CAST(s.Id AS nvarchar(10)) END
    FROM Sensors s
    JOIN probes p ON p.Id = s.Id;

    -- Sensores sin circuito (temperatura y humedad): conservan la clave salvo que ya exista en otra Pi del grupo.
    UPDATE s
    SET TechnicalKey = LEFT(s.TechnicalKey, 80) + N'~' + CAST(s.Id AS nvarchar(10))
    FROM Sensors s
    JOIN @merge m ON m.OldId = s.NodeId
    WHERE (s.CircuitId IS NULL OR s.CircuitId NOT IN (SELECT CircuitId FROM @moved))
      AND EXISTS (SELECT 1 FROM Sensors k
                  WHERE k.TechnicalKey = s.TechnicalKey AND k.NodeId <> s.NodeId
                    AND (k.NodeId = m.KeeperId OR k.NodeId IN (SELECT OldId FROM @merge g WHERE g.KeeperId = m.KeeperId)));

    UPDATE s SET NodeId = m.KeeperId FROM Sensors s JOIN @merge m ON m.OldId = s.NodeId;

    -- Bombas: el tipo con el sufijo del circuito.
    UPDATE a SET TechnicalKey = a.Kind + mv.Suffix, NodeId = mv.KeeperId
    FROM Actuators a
    JOIN @moved mv ON mv.CircuitId = a.CircuitId;

    UPDATE a SET TechnicalKey = a.Kind + N'-c' + CAST(a.CircuitId AS nvarchar(10)), NodeId = m.KeeperId
    FROM Actuators a
    JOIN @merge m ON m.OldId = a.NodeId;

    UPDATE c SET NodeId = mv.KeeperId FROM Circuits c JOIN @moved mv ON mv.CircuitId = c.Id;
    UPDATE c SET NodeId = m.KeeperId FROM Circuits c JOIN @merge m ON m.OldId = c.NodeId;

    -- Lecturas: el id externo es único por Pi (lleva el nodo viejo si se repite en otra Pi del grupo).
    UPDATE r
    SET ExternalReadingId = LEFT(r.ExternalReadingId, 64 - LEN(N'-n' + CAST(m.OldId AS nvarchar(10))))
                            + N'-n' + CAST(m.OldId AS nvarchar(10))
    FROM SensorReadings r
    JOIN @merge m ON m.OldId = r.NodeId
    WHERE EXISTS (SELECT 1 FROM SensorReadings k
                  WHERE k.ExternalReadingId = r.ExternalReadingId AND k.NodeId <> r.NodeId
                    AND (k.NodeId = m.KeeperId OR k.NodeId IN (SELECT OldId FROM @merge g WHERE g.KeeperId = m.KeeperId)));

    UPDATE r SET NodeId = m.KeeperId FROM SensorReadings r JOIN @merge m ON m.OldId = r.NodeId;

    -- Batches (únicos por Pi) y sus rechazos.
    DECLARE @batches TABLE (OldId int NOT NULL, BatchId nvarchar(64) NOT NULL, NewBatchId nvarchar(64) NOT NULL);
    INSERT INTO @batches (OldId, BatchId, NewBatchId)
    SELECT m.OldId, b.BatchId,
           LEFT(b.BatchId, 64 - LEN(N'-n' + CAST(m.OldId AS nvarchar(10)))) + N'-n' + CAST(m.OldId AS nvarchar(10))
    FROM TelemetryBatches b
    JOIN @merge m ON m.OldId = b.NodeId
    WHERE EXISTS (SELECT 1 FROM TelemetryBatches k
                  WHERE k.BatchId = b.BatchId AND k.NodeId <> b.NodeId
                    AND (k.NodeId = m.KeeperId OR k.NodeId IN (SELECT OldId FROM @merge g WHERE g.KeeperId = m.KeeperId)));

    UPDATE b SET BatchId = x.NewBatchId
    FROM TelemetryBatches b
    JOIN @batches x ON x.OldId = b.NodeId AND x.BatchId = b.BatchId;

    UPDATE r SET BatchId = x.NewBatchId
    FROM TelemetryRejections r
    JOIN @batches x ON x.OldId = r.NodeId AND x.BatchId = r.BatchId;

    UPDATE b SET NodeId = m.KeeperId FROM TelemetryBatches b JOIN @merge m ON m.OldId = b.NodeId;
    UPDATE r SET NodeId = m.KeeperId FROM TelemetryRejections r JOIN @merge m ON m.OldId = r.NodeId;

    UPDATE c SET NodeId = m.KeeperId FROM ActuatorCommands c JOIN @merge m ON m.OldId = c.NodeId;
    UPDATE f SET NodeId = m.KeeperId FROM SensorFaults f JOIN @merge m ON m.OldId = f.NodeId;
    UPDATE e SET NodeId = m.KeeperId FROM AnomalyEvents e JOIN @merge m ON m.OldId = e.NodeId;

    -- Alertas abiertas de 'nodo desconectado' de las Pis que se van (huella node-offline-{nodo}-...).
    UPDATE a SET IsResolved = 1, ResolvedAtUtc = SYSUTCDATETIME()
    FROM NotificationAlerts a
    JOIN @merge m ON a.Fingerprint LIKE N'node-offline-' + CAST(m.OldId AS nvarchar(10)) + N'-%'
    WHERE a.IsResolved = 0;

    -- La Pi que queda se lleva el último contacto de las que se van.
    UPDATE k SET
        LastConnection = CASE WHEN x.LastConnection > ISNULL(k.LastConnection, '0001-01-01') THEN x.LastConnection ELSE k.LastConnection END,
        LastAcceptedAt = CASE WHEN x.LastAcceptedAt > ISNULL(k.LastAcceptedAt, '0001-01-01') THEN x.LastAcceptedAt ELSE k.LastAcceptedAt END,
        LastRejectedAt = CASE WHEN x.LastRejectedAt > ISNULL(k.LastRejectedAt, '0001-01-01') THEN x.LastRejectedAt ELSE k.LastRejectedAt END,
        LastCommandPollAtUtc = CASE WHEN x.LastCommandPollAtUtc > ISNULL(k.LastCommandPollAtUtc, '0001-01-01') THEN x.LastCommandPollAtUtc ELSE k.LastCommandPollAtUtc END
    FROM IotNodes k
    JOIN (SELECT m.KeeperId,
                 MAX(n.LastConnection) AS LastConnection,
                 MAX(n.LastAcceptedAt) AS LastAcceptedAt,
                 MAX(n.LastRejectedAt) AS LastRejectedAt,
                 MAX(n.LastCommandPollAtUtc) AS LastCommandPollAtUtc
          FROM IotNodes n
          JOIN @merge m ON m.OldId = n.Id
          GROUP BY m.KeeperId) x ON x.KeeperId = k.Id;

    DELETE FROM IotNodes WHERE Id IN (SELECT OldId FROM @merge);
END
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Solo cambió datos: no se puede saber qué circuitos estaban en otra Pi.
        }
    }
}
