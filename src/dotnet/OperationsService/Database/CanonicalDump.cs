using System.Globalization;
using System.Text;
using Npgsql;

namespace Mair.OperationsService.Database;

/// <summary>
/// The canonical export that defines "byte-identical" for AC-028 and AC-046
/// (<c>OPERATIONAL_DATA.md</c> §9a, <c>OD-014</c>).
/// <para>
/// <b>Formatted here, from typed values</b>, not by <c>COPY</c>: <c>COPY</c>'s text output follows
/// the session's <c>DateStyle</c>, <c>TimeZone</c> and <c>extra_float_digits</c>, so two sessions
/// could dump the same rows differently. Npgsql reads values in the binary protocol, and every type
/// is given one text form below. A type with no rule fails the dump rather than being guessed.
/// </para>
/// </summary>
public static class CanonicalDump
{
    /// <summary>Every table of the schema except its migration history, by name.</summary>
    public static async Task<string> DumpSchemaAsync(NpgsqlConnection conn, string schema, CancellationToken cancellationToken = default)
    {
        var tables = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = $1 AND table_type = 'BASE TABLE' AND table_name <> 'schema_migrations' ORDER BY table_name COLLATE \"C\"", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = schema });
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var dump = new StringBuilder();
        foreach (var table in tables)
        {
            dump.Append(await DumpTableAsync(conn, schema, table, cancellationToken));
        }

        return dump.ToString();
    }

    /// <summary>
    /// Header <c>-- schema.table</c>, then one line per row: every column in declaration order,
    /// tab-separated, rows ordered by primary key.
    /// </summary>
    public static async Task<string> DumpTableAsync(NpgsqlConnection conn, string schema, string table, CancellationToken cancellationToken = default)
    {
        var qualified = $"{Quote(schema)}.{Quote(table)}";

        var columns = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = $1 AND table_name = $2 ORDER BY ordinal_position", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = schema });
            cmd.Parameters.Add(new NpgsqlParameter { Value = table });
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(0));
            }
        }

        var key = new List<string>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT a.attname
            FROM pg_index i
            JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY (i.indkey)
            WHERE i.indrelid = $1::regclass AND i.indisprimary
            ORDER BY array_position(i.indkey, a.attnum)
            """, conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = qualified });
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                key.Add(reader.GetString(0));
            }
        }

        if (key.Count == 0)
        {
            // Without a key the row order is the storage order, and two equal tables can dump differently.
            throw new InvalidOperationException($"{qualified} has no primary key, so it has no canonical row order");
        }

        var dump = new StringBuilder();
        dump.Append("-- ").Append(schema).Append('.').Append(table).Append('\n');

        var select = $"SELECT {string.Join(", ", columns.Select(Quote))} FROM {qualified} ORDER BY {string.Join(", ", key.Select(Quote))}";
        await using (var cmd = new NpgsqlCommand(select, conn))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (i > 0)
                    {
                        dump.Append('\t');
                    }

                    dump.Append(Format(reader.IsDBNull(i) ? null : reader.GetValue(i), qualified, columns[i]));
                }

                dump.Append('\n');
            }
        }

        return dump.ToString();
    }

    internal static string Format(object? value, string table, string column) => value switch
    {
        null => "\\N",
        bool b => b ? "true" : "false",
        short or int or long => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        // .NET's default double formatting is the shortest text that round-trips.
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        Guid g => g.ToString("D"),
        DateTime t => Timestamp(t, table, column),
        string s => Escape(s),
        _ => throw new NotSupportedException(
            $"{table}.{column} is {value.GetType().Name}, which has no canonical text form; add one deliberately rather than guess"),
    };

    private static string Timestamp(DateTime t, string table, string column)
    {
        if (t.Kind != DateTimeKind.Utc)
        {
            throw new NotSupportedException($"{table}.{column} is not timestamptz - only UTC instants have a canonical form");
        }

        // §9a fixes millisecond precision. A finer value would be silently cut, and two rows that
        // differ below a millisecond would dump equal - so it is refused instead.
        if (t.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new NotSupportedException($"{table}.{column} holds {t:O}, finer than the millisecond precision the canonical dump defines");
        }

        return t.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal)
         .Replace("\t", "\\t", StringComparison.Ordinal)
         .Replace("\n", "\\n", StringComparison.Ordinal)
         .Replace("\r", "\\r", StringComparison.Ordinal);

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
