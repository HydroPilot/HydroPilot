using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HydroPilotWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddAnomalyPeakValue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PeakValue",
                table: "AnomalyEvents",
                type: "decimal(12,4)",
                nullable: true);

            // Episodios existentes: la peor lectura usable de su sensor entre el inicio y la última observación
            // (la mínima si quedó abajo de la banda, la máxima si quedó arriba); sin lecturas, la última observada.
            migrationBuilder.Sql(@"
UPDATE e SET PeakValue = COALESCE(
    CASE
        WHEN e.OperationalMin IS NOT NULL AND e.ObservedValue < e.OperationalMin THEN
            (SELECT MIN(r.Value) FROM SensorReadings r
             WHERE r.SensorId = e.SensorId AND r.ObservedAtUtc >= e.FirstObservedAtUtc AND r.ObservedAtUtc <= e.LastObservedAtUtc
               AND r.Quality IN ('VALID', 'SUSPECT', 'STALE'))
        WHEN e.OperationalMax IS NOT NULL AND e.ObservedValue > e.OperationalMax THEN
            (SELECT MAX(r.Value) FROM SensorReadings r
             WHERE r.SensorId = e.SensorId AND r.ObservedAtUtc >= e.FirstObservedAtUtc AND r.ObservedAtUtc <= e.LastObservedAtUtc
               AND r.Quality IN ('VALID', 'SUSPECT', 'STALE'))
    END,
    e.ObservedValue)
FROM AnomalyEvents e;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PeakValue",
                table: "AnomalyEvents");
        }
    }
}
