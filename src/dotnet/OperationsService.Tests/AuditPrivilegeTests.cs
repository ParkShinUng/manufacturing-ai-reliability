using Mair.OperationsService.Database;
using Npgsql;

namespace Mair.OperationsService.Tests;

/// <summary>
/// <b>AUDIT-001 / AC-029</b>, the parts OD-023 added: no runtime role can change an audit row — from
/// the catalogue and by attempting it — and retention drops only whole expired partitions of the
/// allow-listed tables, as <c>mair_ops_retention</c>.
/// </summary>
public sealed class AuditPrivilegeTests(PostgresDatabase postgres) : IClassFixture<PostgresDatabase>
{
    private static readonly string[] Roles = [OperationsRoles.Projector, OperationsRoles.Reader, OperationsRoles.Retention];

    private async Task<(NpgsqlDataSource Owner, string Database)> MigratedAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        var owner = postgres.DataSource(database);
        await new MigrationRunner(owner, "operations", 1).RunAsync(MigrationRunner.Embedded("operations"));
        return (owner, database);
    }

    private static async Task<object?> ScalarAsync(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return await cmd.ExecuteScalarAsync();
    }

    [Fact]
    public async Task NoRuntimeRoleHoldsUpdateOrDeleteOnAnAuditTable_PerTheCatalogue()
    {
        var (owner, _) = await MigratedAsync();
        await using var _ = owner;

        foreach (var role in Roles)
        {
            foreach (var table in new[] { "safety_decision", "control_outcome" })
            {
                foreach (var privilege in new[] { "UPDATE", "DELETE", "TRUNCATE" })
                {
                    Assert.False((bool)(await ScalarAsync(owner, $"SELECT has_table_privilege('{role}', 'operations.{table}', '{privilege}')"))!,
                        $"{role} holds {privilege} on {table}");
                }
            }
        }

        // The projector may insert audit rows; the reader may not write at all; retention may not read.
        Assert.True((bool)(await ScalarAsync(owner, $"SELECT has_table_privilege('{OperationsRoles.Projector}', 'operations.safety_decision', 'INSERT')"))!);
        Assert.False((bool)(await ScalarAsync(owner, $"SELECT has_table_privilege('{OperationsRoles.Reader}', 'operations.telemetry_reading_1s', 'INSERT')"))!);
        Assert.False((bool)(await ScalarAsync(owner, $"SELECT has_table_privilege('{OperationsRoles.Retention}', 'operations.prediction', 'SELECT')"))!);
    }

    public static TheoryData<string, string> Refused => new()
    {
        { OperationsRoles.Projector, "UPDATE operations.safety_decision SET decision = 'ACCEPT'" },
        { OperationsRoles.Projector, "DELETE FROM operations.control_outcome" },
        { OperationsRoles.Projector, "TRUNCATE operations.safety_decision" },
        { OperationsRoles.Reader, "DELETE FROM operations.telemetry_reading_1s" },
        { OperationsRoles.Reader, "UPDATE operations.equipment_state_history SET state = 'IDLE'" },
        { OperationsRoles.Retention, "SELECT count(*) FROM operations.prediction" },
        { OperationsRoles.Retention, "DELETE FROM operations.safety_decision" },
        { OperationsRoles.Reader, "SELECT operations.drop_expired_partitions(now())" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task EachForbiddenStatementIsRefusedByTheServer(string role, string sql)
    {
        var (owner, database) = await MigratedAsync();
        await using var _ = owner;
        await using var asRole = OperationsRoles.DataSource(postgres.ConnectionString(database), role);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(asRole, sql));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task RetentionDropsWholeExpiredPartitionsOfTheAllowListOnly()
    {
        var (owner, database) = await MigratedAsync();
        await using var _ = owner;

        string[] partitions =
        [
            "telemetry_reading_1s|2026-08-01", "telemetry_reading_1s|2026-09-15",
            "safety_decision|2026-06-15", "safety_decision|2026-07-15",
            "control_outcome|2025-09-15", "control_outcome|2025-10-15",
            "equipment_state_history|2025-09-15", "equipment_state_history|2026-01-15",
        ];
        foreach (var p in partitions)
        {
            var parts = p.Split('|');
            await ScalarAsync(owner, $"SELECT operations.ensure_partition('{parts[0]}', '{parts[1]}T12:00:00Z')");
        }

        await ScalarAsync(owner, "INSERT INTO operations.equipment_decommission VALUES ('eq-001', '2020-01-01T00:00:00Z', 't', 0, 1)");

        await using var asRetention = OperationsRoles.DataSource(postgres.ConnectionString(database), OperationsRoles.Retention);
        var job = new RetentionJob(asRetention, new FixedClock(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero)));
        var dropped = await job.RunOnceAsync();

        // Upper bound older than the period: readings 30 d, decisions 90 d, outcomes and state history 1 y.
        Assert.Equal(
            ["control_outcome_p202509", "equipment_state_history_p202509", "safety_decision_p202606", "telemetry_reading_1s_p20260801"],
            dropped.Order(StringComparer.Ordinal));
        Assert.Empty(await job.RunOnceAsync());
        Assert.Equal(1L, await ScalarAsync(owner, "SELECT count(*) FROM operations.equipment_decommission"));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
