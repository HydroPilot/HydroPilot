using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HydroPilotWeb.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace HydroPilotWeb.Services.SimulationEnvironment;

/// <summary>
/// Carga del historial con sus Id: de a <see cref="RowsPerStatement"/> filas, como un JSON que SQL Server abre con
/// OPENJSON (<c>INSERT … SELECT … FROM OPENJSON(@json)</c>), con tablas, columnas, tipos, conversiones y decimales del
/// modelo de EF.
/// No usa SqlBulkCopy a propósito: en una máquina con poca RAM, LocalDB arma un fondo de memoria para consultas de
/// pocos MB; la carga masiva estima en grande, pide más memoria que todo el fondo y queda esperando, y como la cola es
/// en orden, detrás esperan también las consultas de la app. OPENJSON hace que SQL Server estime pocas filas y pida
/// casi nada. Las claves foráneas se apagan durante la carga (las tablas se cargan en cualquier orden).
/// </summary>
internal static class BulkWriter
{
    public const int RowsPerStatement = 1_000;

    public static Task WriteAsync<T>(HydroPilotDbContext context, IReadOnlyCollection<T> rows, CancellationToken ct) where T : class =>
        rows.Count == 0 ? Task.CompletedTask : Task.Run(() => Write(context, rows, ct), ct);

    /// <summary>Lo mismo, sincrónico: para descargar lo generado mientras se genera el historial (en un hilo aparte).</summary>
    public static void Write<T>(HydroPilotDbContext context, IReadOnlyCollection<T> rows, CancellationToken ct = default) where T : class
    {
        if (rows.Count == 0)
            return;

        var entity = context.Model.FindEntityType(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} no es una entidad.");
        var tableName = entity.GetTableName()!;
        var store = StoreObjectIdentifier.Table(tableName, entity.GetSchema());
        var table = $"[{entity.GetSchema() ?? "dbo"}].[{tableName}]";
        var columns = entity.GetProperties()
            .Where(p => p.PropertyInfo is not null && p.GetColumnName(store) is not null)
            .Select(p => new Column(p, p.GetColumnName(store)!, p.GetColumnType(store)))
            .ToList();

        var connection = (SqlConnection)context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            connection.Open();

        var list = string.Join(", ", columns.Select(c => $"[{c.Name}]"));
        var with = string.Join(", ", columns.Select((c, i) => $"[{c.Name}] {c.SqlType} '$[{i}]'"));
        var insert = $"INSERT INTO {table} ({list}) SELECT {list} FROM OPENJSON(@json) WITH ({with});";
        var sql = HasIdentity(connection, table)
            ? $"SET IDENTITY_INSERT {table} ON; {insert} SET IDENTITY_INSERT {table} OFF;"
            : insert;

        foreach (var chunk in rows.Chunk(RowsPerStatement))
        {
            ct.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 600;
            command.Parameters.Add(new SqlParameter("@json", SqlDbType.NVarChar, -1) { Value = Json(chunk, columns) });
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Antes de cargar: claves foráneas apagadas en esas tablas.</summary>
    public static async Task BeginLoadAsync(HydroPilotDbContext context, IEnumerable<Type> types, CancellationToken ct)
    {
        // Los nombres de tabla salen del modelo de EF, no de una entrada del usuario.
        foreach (var table in Tables(context, types))
        {
            var sql = $"ALTER TABLE {table} NOCHECK CONSTRAINT ALL";
            await context.Database.ExecuteSqlRawAsync(sql, ct);
        }
    }

    /// <summary>
    /// Después de cargar: claves foráneas prendidas otra vez. No se revalidan (eso también pide memoria para unir tablas
    /// grandes): el historial las respeta por construcción y los tests de integración lo verifican con DBCC.
    /// </summary>
    public static async Task EndLoadAsync(HydroPilotDbContext context, IEnumerable<Type> types, CancellationToken ct)
    {
        foreach (var table in Tables(context, types))
        {
            var sql = $"ALTER TABLE {table} CHECK CONSTRAINT ALL";
            await context.Database.ExecuteSqlRawAsync(sql, ct);
        }
    }

    private static IEnumerable<string> Tables(HydroPilotDbContext context, IEnumerable<Type> types) =>
        types.Select(type => context.Model.FindEntityType(type))
            .OfType<IEntityType>()
            .Where(e => e.GetTableName() is not null)
            .Select(e => $"[{e.GetSchema() ?? "dbo"}].[{e.GetTableName()}]");

    private static bool HasIdentity(SqlConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT OBJECTPROPERTY(OBJECT_ID('{table.Replace("'", "''")}'), 'TableHasIdentity')";
        return command.ExecuteScalar() is int value && value == 1;
    }

    /// <summary>Las filas como arreglos JSON (una posición por columna, en el orden de la lista).</summary>
    private static string Json<T>(IEnumerable<T> rows, IReadOnlyList<Column> columns)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                writer.WriteStartArray();
                foreach (var column in columns)
                    column.Write(writer, row!);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class Column
    {
        private readonly IProperty _property;
        private readonly Func<object?, object?>? _convert;
        private readonly int? _scale;

        public Column(IProperty property, string name, string? sqlType)
        {
            _property = property;
            Name = name;
            SqlType = sqlType ?? property.GetColumnType();
            var converter = property.GetValueConverter() ?? property.GetTypeMapping().Converter;
            _convert = converter is null ? null : converter.ConvertToProvider;
            var provider = converter?.ProviderClrType ?? property.ClrType;
            provider = Nullable.GetUnderlyingType(provider) ?? provider;
            _scale = provider == typeof(decimal) ? Scale(SqlType, property) : null;
        }

        public string Name { get; }
        public string SqlType { get; }

        public void Write(Utf8JsonWriter writer, object row)
        {
            var value = _property.PropertyInfo!.GetValue(row);
            if (value is not null && _convert is not null)
                value = _convert(value);
            switch (value)
            {
                case null:
                    writer.WriteNullValue();
                    break;
                case string text:
                    writer.WriteStringValue(text);
                    break;
                case int number:
                    writer.WriteNumberValue(number);
                    break;
                case long number:
                    writer.WriteNumberValue(number);
                    break;
                case decimal number:
                    writer.WriteNumberValue(_scale is { } scale ? Math.Round(number, scale, MidpointRounding.AwayFromZero) : number);
                    break;
                case double number:
                    writer.WriteNumberValue(number);
                    break;
                case bool flag:
                    writer.WriteBooleanValue(flag);
                    break;
                case DateTime moment:
                    writer.WriteStringValue(moment.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture));
                    break;
                case DateOnly date:
                    writer.WriteStringValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    break;
                case TimeSpan time:
                    writer.WriteStringValue(time.ToString("c", CultureInfo.InvariantCulture));
                    break;
                case TimeOnly time:
                    writer.WriteStringValue(time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
                    break;
                case Guid id:
                    writer.WriteStringValue(id);
                    break;
                default:
                    writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
            }
        }

        /// <summary>Decimales de la columna: de "decimal(p,s)" o, sin tipo explícito, los de EF (18,2).</summary>
        private static int Scale(string sqlType, IProperty property)
        {
            if (property.GetScale() is { } scale)
                return scale;
            var comma = sqlType.IndexOf(',');
            return comma > 0 && int.TryParse(sqlType[(comma + 1)..].TrimEnd(')'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 2;
        }
    }
}
