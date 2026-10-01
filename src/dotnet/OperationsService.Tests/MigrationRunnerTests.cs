using Mair.OperationsService.Database;
using Npgsql;

namespace Mair.OperationsService.Tests;

/// <summary>
/// The forward-only runner, ADR-0024 B5, and B4 underneath it: every test here starts by using the
/// container, so a readiness check that passed on the init server would fail them all. Each test
/// takes a database of its own.
/// </summary>
public sealed class MigrationRunnerTests(PostgresDatabase postgres) : IClassFixture<PostgresDatabase>
{
    private static readonly Migration[] Two =
    [
        new(1, "create_a", "CREATE TABLE a (id integer PRIMARY KEY);"),
        new(2, "create_b", "CREATE TABLE b (id integer PRIMARY KEY);"),
    ];

    private async Task<NpgsqlDataSource> FreshAsync() => postgres.DataSource(await postgres.CreateDatabaseAsync());

    private static async Task<List<string>> TablesAsync(NpgsqlDataSource db, string schema)
    {
        await using var cmd = db.CreateCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = $1 ORDER BY 1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = schema });
        var tables = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    [Fact]
    public async Task TheServerIsTheOnePinned()
    {
        await using var db = postgres.DataSource();
        await using var cmd = db.CreateCommand("SHOW server_version");
        Assert.StartsWith("18.6", (string)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task UpFromEmpty_ThenASecondRunAppliesNothing()
    {
        await using var db = await FreshAsync();
        var runner = new MigrationRunner(db, "operations", 1);

        Assert.Equal([1, 2], await runner.RunAsync(Two));
        Assert.Empty(await runner.RunAsync(Two));
        Assert.Equal(["a", "b", "schema_migrations"], await TablesAsync(db, "operations"));
    }

    [Fact]
    public async Task AFailingMigrationLeavesNoPartialSchema()
    {
        await using var db = await FreshAsync();
        var runner = new MigrationRunner(db, "operations", 1);
        await runner.RunAsync(Two[..1]);

        // The first statement would succeed; the second fails. Nothing of the file may remain.
        Migration[] broken = [Two[0], new(2, "half", "CREATE TABLE c (id integer PRIMARY KEY); CREATE TABLE c (id integer);")];
        var ex = await Assert.ThrowsAsync<MigrationException>(() => runner.RunAsync(broken));

        Assert.Contains("migration 2", ex.Message);
        Assert.Equal(["a", "schema_migrations"], await TablesAsync(db, "operations"));
    }

    [Fact]
    public async Task NonTransactionalDdlFailsTheMigration_ByPostgresqlsOwnRule()
    {
        await using var db = await FreshAsync();
        Migration[] concurrently = [Two[0], new(2, "online_index", "CREATE INDEX CONCURRENTLY a_id ON a (id);")];

        await Assert.ThrowsAsync<MigrationException>(() => new MigrationRunner(db, "operations", 1).RunAsync(concurrently));
        Assert.Equal(["a", "schema_migrations"], await TablesAsync(db, "operations"));
    }

    [Fact]
    public async Task AnAppliedFileThatWasEditedStopsTheRun()
    {
        await using var db = await FreshAsync();
        var runner = new MigrationRunner(db, "operations", 1);
        await runner.RunAsync(Two);

        Migration[] edited = [Two[0], Two[1] with { Sql = "CREATE TABLE b (id bigint PRIMARY KEY);" }];
        var ex = await Assert.ThrowsAsync<MigrationException>(() => runner.RunAsync(edited));
        Assert.Contains("edited after it was applied", ex.Message);
    }

    [Fact]
    public async Task AGapInVersions_AndAnAppliedVersionWithNoFile_AreRefused()
    {
        await using var db = await FreshAsync();
        var runner = new MigrationRunner(db, "operations", 1);

        await Assert.ThrowsAsync<MigrationException>(() => runner.RunAsync([Two[1]]));

        await runner.RunAsync(Two);
        var ex = await Assert.ThrowsAsync<MigrationException>(() => runner.RunAsync(Two[..1]));
        Assert.Contains("has no file", ex.Message);
    }

    [Fact]
    public async Task TwoRunnersStartedTogetherApplyEachFileOnce()
    {
        await using var db = await FreshAsync();
        // A migration slow enough that the two runs overlap without the lock.
        Migration[] slow = [new(1, "slow", "SELECT pg_sleep(1); CREATE TABLE a (id integer PRIMARY KEY);"), Two[1]];

        var runs = await Task.WhenAll(
            Task.Run(() => new MigrationRunner(db, "operations", 1).RunAsync(slow)),
            Task.Run(() => new MigrationRunner(db, "operations", 1).RunAsync(slow)));

        Assert.Equal([1, 2], runs.SelectMany(r => r).Order());
        await using var cmd = db.CreateCommand("SELECT count(*) FROM operations.schema_migrations");
        Assert.Equal(2L, await cmd.ExecuteScalarAsync());
    }
}
