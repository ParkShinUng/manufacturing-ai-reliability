using Mair.OperationsService.Database;
using Npgsql;

namespace Mair.OperationsService.Tests;

/// <summary>
/// The <c>operations</c> schema as migrated, checked against the rules the canonical dump depends
/// on (OD-014) by reading the catalogue — not by reading the migration file and agreeing with it.
/// This is the catalogue half of PROJ-002.
/// </summary>
public sealed class OperationsSchemaTests(PostgresDatabase postgres) : IClassFixture<PostgresDatabase>
{
    private static readonly string[] ProjectionTables =
    [
        "authorization_watermark", "control_outcome", "equipment_decommission", "equipment_state_history", "model_deployment",
        "prediction", "safety_decision", "telemetry_reading_1s",
    ];

    private async Task<NpgsqlDataSource> MigratedAsync()
    {
        var db = postgres.DataSource(await postgres.CreateDatabaseAsync());
        await new MigrationRunner(db, "operations", 1).RunAsync(MigrationRunner.Embedded("operations"));
        return db;
    }

    private static async Task<List<string>> ColumnAsync(NpgsqlDataSource db, string sql, params object[] args)
    {
        await using var cmd = db.CreateCommand(sql);
        foreach (var arg in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        var values = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static Task<List<string>> LogicalTablesAsync(NpgsqlDataSource db) => ColumnAsync(db, """
        SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'operations' AND c.relkind IN ('r', 'p') AND NOT c.relispartition AND c.relname <> 'schema_migrations'
        ORDER BY c.relname COLLATE "C"
        """);

    [Fact]
    public async Task TheEmbeddedMigrationsCreateTheProjectionTables_AndNoFaultTableYet()
    {
        await using var db = await MigratedAsync();
        // fault_injection waits for factory.faults.v1 to have a contract (OD-020); equipment_decommission
        // arrives in migration 0002 (OD-021) - forward-only, so a second file, not an edited first.
        Assert.Equal(ProjectionTables, await LogicalTablesAsync(db));
    }

    [Fact]
    public async Task NoProjectionColumnHasADefault_AGeneratedValue_OrAnIdentity()
    {
        await using var db = await MigratedAsync();
        var offenders = await ColumnAsync(db, """
            SELECT table_name || '.' || column_name FROM information_schema.columns
            WHERE table_schema = 'operations' AND table_name <> 'schema_migrations'
              AND (column_default IS NOT NULL OR is_identity = 'YES' OR is_generated <> 'NEVER')
            """);
        Assert.Empty(offenders);

        // The same query does see a default where one exists - the migration history's applied_at_utc,
        // which is not a projection table - so an empty result above is a finding, not a blind spot.
        Assert.Equal(["schema_migrations.applied_at_utc"], await ColumnAsync(db, """
            SELECT table_name || '.' || column_name FROM information_schema.columns
            WHERE table_schema = 'operations' AND table_name = 'schema_migrations'
              AND (column_default IS NOT NULL OR is_identity = 'YES' OR is_generated <> 'NEVER')
            """));

        Assert.Empty(await ColumnAsync(db, "SELECT sequence_name FROM information_schema.sequences WHERE sequence_schema = 'operations'"));
    }

    [Fact]
    public async Task EveryProjectionTableHasAPrimaryKey_AndItsProvenance()
    {
        await using var db = await MigratedAsync();
        foreach (var table in ProjectionTables)
        {
            var key = await ColumnAsync(db, """
                SELECT a.attname FROM pg_index i
                JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY (i.indkey)
                WHERE i.indrelid = ('operations.' || $1)::regclass AND i.indisprimary
                """, table);
            Assert.True(key.Count > 0, $"{table} has no primary key");

            var provenance = await ColumnAsync(db, """
                SELECT column_name FROM information_schema.columns
                WHERE table_schema = 'operations' AND table_name = $1 AND is_nullable = 'NO'
                  AND column_name IN ('source_topic', 'source_partition', 'source_offset')
                ORDER BY column_name
                """, table);
            Assert.True(provenance.Count == 3, $"{table} lacks non-null provenance: has [{string.Join(", ", provenance)}]");

            // A range rebuild finds its rows by provenance (OD-014); without the index it scans.
            var indexed = await ColumnAsync(db, """
                SELECT i.relname FROM pg_index x
                JOIN pg_class i ON i.oid = x.indexrelid
                WHERE x.indrelid = ('operations.' || $1)::regclass
                  AND pg_get_indexdef(x.indexrelid) LIKE '%(source_topic, source_partition, source_offset)%'
                """, table);
            Assert.True(indexed.Count == 1, $"{table} has no provenance index");
        }
    }

    [Fact]
    public async Task PartitionsAreDailyForThirtyDayTables_MonthlyForAudit_InUtc_AndIdempotent()
    {
        await using var db = await MigratedAsync();

        async Task<string> Ensure(string table, string at)
        {
            await using var cmd = db.CreateCommand("SELECT operations.ensure_partition($1, $2::timestamptz)");
            cmd.Parameters.Add(new NpgsqlParameter { Value = table });
            cmd.Parameters.Add(new NpgsqlParameter { Value = at });
            return (string)(await cmd.ExecuteScalarAsync())!;
        }

        // 23:30 at -05:00 is 04:30 UTC the next day: the partition follows UTC, not the session.
        Assert.Equal("telemetry_reading_1s_p20261002", await Ensure("telemetry_reading_1s", "2026-10-01T23:30:00-05:00"));
        Assert.Equal("prediction_p20261001", await Ensure("prediction", "2026-10-01T00:00:00Z"));
        Assert.Equal("safety_decision_p202610", await Ensure("safety_decision", "2026-10-31T23:59:59.999Z"));
        Assert.Equal("safety_decision_p202610", await Ensure("safety_decision", "2026-10-01T00:00:00Z"));

        var bounds = await ColumnAsync(db, """
            SELECT pg_get_expr(c.relpartbound, c.oid) FROM pg_class c
            WHERE c.relname IN ('telemetry_reading_1s_p20261002', 'safety_decision_p202610') ORDER BY c.relname
            """);
        Assert.Contains("2026-11-01 00:00:00+00", bounds[0]);
        Assert.Contains("2026-10-02 00:00:00+00", bounds[1]);

        await using var bad = db.CreateCommand("SELECT operations.ensure_partition('model_deployment', now())");
        await Assert.ThrowsAsync<PostgresException>(() => bad.ExecuteScalarAsync());
    }

    [Fact]
    public async Task APartitionedTableIsDumpedOnce_ThroughItsParent()
    {
        await using var db = await MigratedAsync();
        await using (var cmd = db.CreateCommand("""
            SELECT operations.ensure_partition('equipment_state_history', '2026-09-15T00:00:00Z');
            SELECT operations.ensure_partition('equipment_state_history', '2026-10-15T00:00:00Z');
            INSERT INTO operations.equipment_state_history VALUES
              ('eq-001', 1, 1, '2026-09-15T00:00:00Z', 'RUNNING', NULL, NULL, true, 'gw-1', '7b52009b-64fd-4a1f-9b1e-0a2f4c6d8e10', 'factory.equipment-states.v1', 0, 10),
              ('eq-001', 1, 2, '2026-10-15T00:00:00Z', 'IDLE', 'RUNNING', 'T3', true, 'gw-1', '0f1e2d3c-4b5a-4698-8877-665544332211', 'factory.equipment-states.v1', 0, 11);
            """))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        await using var conn = await db.OpenConnectionAsync();
        var dump = await CanonicalDump.DumpSchemaAsync(conn, "operations");

        Assert.Equal(ProjectionTables.Select(t => $"-- operations.{t}"), dump.Split('\n').Where(l => l.StartsWith("--", StringComparison.Ordinal)));
        Assert.Equal(2, dump.Split('\n').Count(l => l.StartsWith("eq-001", StringComparison.Ordinal)));
    }
}
