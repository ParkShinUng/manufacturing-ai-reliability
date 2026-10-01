using Mair.OperationsService.Database;
using Npgsql;

namespace Mair.OperationsService.Tests;

/// <summary>ADR-0024 B6: the dump is a property of the rows, not of the session that reads them.</summary>
public sealed class CanonicalDumpTests(PostgresDatabase postgres) : IClassFixture<PostgresDatabase>
{
    private const string Table = """
        CREATE TABLE sample (
            id        uuid PRIMARY KEY,
            at_utc    timestamptz NOT NULL,
            value     double precision,
            ratio     numeric(6,3),
            flag      boolean,
            note      text,
            seq       bigint NOT NULL);
        INSERT INTO sample VALUES
            ('7b52009b-64fd-4a1f-9b1e-0a2f4c6d8e10', '2026-10-01T03:04:05.678Z', 0.1, 1.500, true, E'tab\there\nline', 9007199254740993),
            ('0f1e2d3c-4b5a-4698-8877-665544332211', '2026-10-01T03:04:05.000Z', 1e-7, NULL, false, NULL, 1),
            ('aaaaaaaa-0000-4000-8000-000000000000', '1999-12-31T23:59:59.999Z', 12345.678901234567, -0.001, NULL, 'back\slash', -1);
        """;

    private async Task<string> SeededAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var conn = new NpgsqlConnection(postgres.ConnectionString(database));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE SCHEMA operations; SET search_path TO operations; {Table}", conn);
        await cmd.ExecuteNonQueryAsync();
        return database;
    }

    private async Task<string> DumpUnderAsync(string database, string settings)
    {
        await using var conn = new NpgsqlConnection(postgres.ConnectionString(database));
        await conn.OpenAsync();
        await using (var set = new NpgsqlCommand(settings, conn))
        {
            await set.ExecuteNonQueryAsync();
        }

        return await CanonicalDump.DumpSchemaAsync(conn, "operations");
    }

    [Fact]
    public async Task TheSameRowsDumpToTheSameBytesUnderDifferentSessionSettings()
    {
        var database = await SeededAsync();

        var a = await DumpUnderAsync(database, "SET TimeZone = 'UTC'; SET DateStyle = 'ISO, YMD'; SET extra_float_digits = 0");
        var b = await DumpUnderAsync(database, "SET TimeZone = 'Asia/Seoul'; SET DateStyle = 'SQL, DMY'; SET extra_float_digits = 3");
        var c = await DumpUnderAsync(database, "SET TimeZone = 'America/Los_Angeles'; SET DateStyle = 'German'; SET extra_float_digits = -15");

        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    [Fact]
    public async Task TheFormIsTheOneTheDocumentDefines()
    {
        var dump = await DumpUnderAsync(await SeededAsync(), "SET TimeZone = 'Asia/Seoul'");

        // Ordered by primary key (uuid order), every column in declaration order, tab-separated,
        // UTC with milliseconds and Z, shortest round-trip doubles, \N for null, escapes for tab,
        // newline and backslash (OPERATIONAL_DATA.md §9a).
        Assert.Equal(
            "-- operations.sample\n" +
            "0f1e2d3c-4b5a-4698-8877-665544332211\t2026-10-01T03:04:05.000Z\t1E-07\t\\N\tfalse\t\\N\t1\n" +
            "7b52009b-64fd-4a1f-9b1e-0a2f4c6d8e10\t2026-10-01T03:04:05.678Z\t0.1\t1.500\ttrue\ttab\\there\\nline\t9007199254740993\n" +
            "aaaaaaaa-0000-4000-8000-000000000000\t1999-12-31T23:59:59.999Z\t12345.678901234567\t-0.001\t\\N\tback\\\\slash\t-1\n",
            dump);
    }

    [Fact]
    public async Task ATimestampFinerThanAMillisecondIsRefused_NotCut()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var conn = new NpgsqlConnection(postgres.ConnectionString(database));
        await conn.OpenAsync();
        await using (var cmd = new NpgsqlCommand(
            "CREATE SCHEMA operations; CREATE TABLE operations.t (id integer PRIMARY KEY, at_utc timestamptz); INSERT INTO operations.t VALUES (1, '2026-10-01T00:00:00.0001Z');", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<NotSupportedException>(() => CanonicalDump.DumpSchemaAsync(conn, "operations"));
    }

    [Fact]
    public async Task ATableWithoutAPrimaryKeyHasNoCanonicalOrder()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var conn = new NpgsqlConnection(postgres.ConnectionString(database));
        await conn.OpenAsync();
        await using (var cmd = new NpgsqlCommand("CREATE SCHEMA operations; CREATE TABLE operations.t (id integer);", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => CanonicalDump.DumpSchemaAsync(conn, "operations"));
    }
}
