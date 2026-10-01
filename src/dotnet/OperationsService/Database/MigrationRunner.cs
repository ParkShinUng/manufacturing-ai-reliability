using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Mair.OperationsService.Database;

/// <summary>One forward-only migration file, <c>NNNN_name.sql</c>.</summary>
public sealed record Migration(int Version, string Name, string Sql)
{
    // Computed, not initialised: an initialiser would be copied by a `with` expression, and a
    // migration whose text changed would keep its old checksum.
    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Sql)));
}

/// <summary>The schema and its migration files disagree, and nothing is repaired automatically.</summary>
public sealed class MigrationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Applies one owner's migrations to one PostgreSQL schema (<c>OD-016</c>, <c>ADR-0024</c>).
/// <para>
/// <b>Forward-only.</b> A down migration of a table that holds audit history is a delete that looks
/// like a rollback, so there are none. <b>One transaction per file</b>: PostgreSQL DDL is
/// transactional, so a failing file leaves the schema exactly as it was, and a statement that cannot
/// run in a transaction (<c>CREATE INDEX CONCURRENTLY</c>) fails the migration by PostgreSQL's own
/// rule — online index builds are a runbook step, not a migration.
/// </para>
/// <para>
/// <b>Serialised</b> by a session advisory lock taken before anything else and held for the whole
/// run, so two services starting together apply each file once.
/// </para>
/// </summary>
public sealed partial class MigrationRunner
{
    /// <summary><c>MAIR</c> in ASCII: the advisory-lock class every owner shares (ADR-0024).</summary>
    public const int LockClass = 1296124242;

    private readonly NpgsqlDataSource _db;
    private readonly string _schema;
    private readonly int _lockId;

    /// <param name="lockId">The schema's fixed number: <c>operations</c> = 1, <c>control</c> = 2.</param>
    public MigrationRunner(NpgsqlDataSource db, string schema, int lockId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!SchemaName().IsMatch(schema))
        {
            throw new ArgumentException($"'{schema}' is not a plain lower-case schema name", nameof(schema));
        }

        _db = db;
        _schema = schema;
        _lockId = lockId;
    }

    /// <summary>The migrations embedded under <c>Migrations/&lt;schema&gt;/</c> in this assembly.</summary>
    public static IReadOnlyList<Migration> Embedded(string schema)
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var prefix = $"{assembly.GetName().Name}.Migrations.{schema}.";
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => Parse(n[prefix.Length..^4], Read(assembly, n)))
            .OrderBy(m => m.Version)
            .ToList();
    }

    /// <summary><c>0001_create_tables</c> → version 1, name <c>create_tables</c>.</summary>
    public static Migration Parse(string fileStem, string sql)
    {
        var match = FileStem().Match(fileStem);
        if (!match.Success)
        {
            throw new MigrationException($"'{fileStem}' is not NNNN_name");
        }

        return new Migration(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), match.Groups[2].Value, sql);
    }

    /// <returns>The versions applied by this run; empty when the schema was already current.</returns>
    public async Task<IReadOnlyList<int>> RunAsync(IReadOnlyList<Migration> migrations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        var ordered = migrations.OrderBy(m => m.Version).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Version != i + 1)
            {
                throw new MigrationException(
                    $"{_schema}: expected migration {i + 1}, found {ordered[i].Version} - versions must run 1, 2, 3 without gaps or repeats");
            }
        }

        await using var conn = await _db.OpenConnectionAsync(cancellationToken);
        await ExecAsync(conn, null, "SELECT pg_advisory_lock($1, $2)", cancellationToken, LockClass, _lockId);
        try
        {
            await using (var tx = await conn.BeginTransactionAsync(cancellationToken))
            {
                await ExecAsync(conn, tx, $"CREATE SCHEMA IF NOT EXISTS {_schema}", cancellationToken);
                // applied_at_utc is operational metadata on a table that is not a projection; OD-014's
                // determinism rule covers projection tables only.
                await ExecAsync(conn, tx, $"""
                    CREATE TABLE IF NOT EXISTS {_schema}.schema_migrations (
                        version        integer PRIMARY KEY,
                        name           text NOT NULL,
                        sha256         text NOT NULL,
                        applied_at_utc timestamptz NOT NULL DEFAULT now())
                    """, cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }

            var applied = new Dictionary<int, (string Name, string Sha256)>();
            await using (var cmd = new NpgsqlCommand($"SELECT version, name, sha256 FROM {_schema}.schema_migrations", conn))
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    applied[reader.GetInt32(0)] = (reader.GetString(1), reader.GetString(2));
                }
            }

            foreach (var (version, (name, sha)) in applied.OrderBy(a => a.Key))
            {
                var file = ordered.FirstOrDefault(m => m.Version == version)
                    ?? throw new MigrationException($"{_schema}: migration {version} ({name}) is recorded as applied but has no file");
                if (file.Sha256 != sha)
                {
                    throw new MigrationException(
                        $"{_schema}: migration {version} ({name}) was edited after it was applied - recorded {sha}, file {file.Sha256}. Write a new migration instead.");
                }
            }

            var ran = new List<int>();
            foreach (var migration in ordered.Where(m => !applied.ContainsKey(m.Version)))
            {
                await using var tx = await conn.BeginTransactionAsync(cancellationToken);
                try
                {
                    await ExecAsync(conn, tx, $"SET LOCAL search_path TO {_schema}", cancellationToken);
                    await ExecAsync(conn, tx, migration.Sql, cancellationToken);
                    await ExecAsync(conn, tx, $"INSERT INTO {_schema}.schema_migrations (version, name, sha256) VALUES ($1, $2, $3)",
                        cancellationToken, migration.Version, migration.Name, migration.Sha256);
                    await tx.CommitAsync(cancellationToken);
                }
                catch (PostgresException ex)
                {
                    throw new MigrationException($"{_schema}: migration {migration.Version} ({migration.Name}) failed and was rolled back: {ex.MessageText}", ex);
                }

                ran.Add(migration.Version);
            }

            return ran;
        }
        finally
        {
            await ExecAsync(conn, null, "SELECT pg_advisory_unlock($1, $2)", CancellationToken.None, LockClass, _lockId);
        }
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken cancellationToken, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var arg in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [GeneratedRegex("^[a-z_]+$")]
    private static partial Regex SchemaName();

    [GeneratedRegex("^([0-9]{4})_([a-z0-9_]+)$")]
    private static partial Regex FileStem();
}
