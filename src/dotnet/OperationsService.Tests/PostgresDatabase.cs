using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Mair.OperationsService.Tests;

/// <summary>
/// PostgreSQL for one test class, on the image <c>ADR-0024</c> pins — tag <b>and</b> digest — started
/// from the Testcontainers core, as the Kafka broker is (ADR-0021).
/// <para>
/// <b>Readiness is a real connection, not a log line</b> (ADR-0024 B4). The image runs a temporary
/// server during initialisation and logs "ready to accept connections" for it too, then restarts.
/// That first server listens on the Unix socket only, so a TCP connection from the host cannot
/// reach it: polling one is what tells the real server from the init one.
/// </para>
/// </summary>
public sealed class PostgresDatabase : IAsyncLifetime
{
    public const string Image =
        "postgres:18.6@sha256:5a5a84b19854a9ffaa54082c166ff4ec27473a361e496e5ea167f298f2da9722";

    // A throwaway credential for a container that lives as long as the test class.
    private readonly string _password = Guid.NewGuid().ToString("N");
    private readonly int _port = FreePort();
    private readonly IContainer _container;

    public PostgresDatabase()
    {
        _container = new ContainerBuilder(Image)
            .WithPortBinding(_port, 5432)
            .WithEnvironment("POSTGRES_PASSWORD", _password)
            .WithEnvironment("POSTGRES_DB", "mair")
            .Build();
    }

    public string ConnectionString(string database = "mair") =>
        $"Host=127.0.0.1;Port={_port};Username=postgres;Password={_password};Database={database};Pooling=true";

    public NpgsqlDataSource DataSource(string database = "mair") => NpgsqlDataSource.Create(ConnectionString(database));

    /// <summary>Stops the server: FAIL-DB-001 and API-001's 503. The port and data survive a restart.</summary>
    public Task StopAsync() => _container.StopAsync();

    public Task StartAsync() => InitializeAsync();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var deadline = DateTime.UtcNow.AddSeconds(60);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var conn = new NpgsqlConnection(ConnectionString() + ";Pooling=false;Timeout=2");
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand("SELECT 1", conn);
                await cmd.ExecuteScalarAsync();
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or SocketException or IOException or TimeoutException)
            {
                last = ex;
                await Task.Delay(250);
            }
        }

        throw new InvalidOperationException("PostgreSQL did not accept a TCP connection within 60 s", last);
    }

    /// <summary>A fresh, empty database on the same server: PROJ-002 rebuilds into one.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = "db_" + Guid.NewGuid().ToString("N")[..12];
        await using var conn = new NpgsqlConnection(ConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
        await cmd.ExecuteNonQueryAsync();
        return name;
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
