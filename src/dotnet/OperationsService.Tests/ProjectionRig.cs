using System.Text;
using System.Text.Json.Nodes;
using Confluent.Kafka;
using Mair.EventBackbone;
using Mair.EventBackbone.Tests;
using Mair.OperationsService.Database;
using Mair.OperationsService.Projection;
using Npgsql;

namespace Mair.OperationsService.Tests;

/// <summary>
/// A broker and a PostgreSQL server for the projection suites. One of each per class: the topics are
/// the register's real ones, so what one class produces the next would otherwise read.
/// </summary>
public sealed class ProjectionRig : IAsyncLifetime
{
    public KafkaBroker Kafka { get; } = new();

    public PostgresDatabase Postgres { get; } = new();

    public static string Root { get; } = FindRoot();

    public static string Contracts => Path.Combine(Root, "contracts", "jsonschema", "v1");

    public async Task InitializeAsync()
    {
        await Task.WhenAll(Kafka.InitializeAsync(), Postgres.InitializeAsync());
        using var admin = Kafka.Admin();
        await new TopicBootstrap(admin).RunAsync(TopicRegister.All);
    }

    public async Task DisposeAsync()
    {
        await Kafka.DisposeAsync();
        await Postgres.DisposeAsync();
    }

    /// <summary>An empty database with the <c>operations</c> schema migrated into it.</summary>
    public async Task<NpgsqlDataSource> MigratedAsync()
    {
        var db = Postgres.DataSource(await Postgres.CreateDatabaseAsync());
        await new MigrationRunner(db, "operations", 1).RunAsync(MigrationRunner.Embedded("operations"));
        return db;
    }

    public IProducer<string, byte[]> Producer() =>
        new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = Kafka.BootstrapServers, Acks = Acks.All, EnableIdempotence = true }).Build();

    public OperationsProjector Projector(NpgsqlDataSource db, IProducer<string, byte[]> deadLetters, string? group = null) =>
        new(Kafka.BootstrapServers, db, Contracts, deadLetters, group ?? "cg.test-projector." + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    /// Runs the projector until every consumer has been caught up (OD-018) after this call began, so
    /// every record produced before it is settled; then stops it. Fails the test past the limit.
    /// </summary>
    public static async Task CatchUpAsync(OperationsProjector projector, TimeSpan? limit = null)
    {
        var started = DateTimeOffset.UtcNow;
        using var stop = new CancellationTokenSource();
        var run = projector.RunAsync(stop.Token);
        var deadline = DateTime.UtcNow + (limit ?? TimeSpan.FromSeconds(90));
        try
        {
            while (!projector.CaughtUpSince(started))
            {
                Assert.True(DateTime.UtcNow < deadline, "the projector did not catch up: " + string.Join(" | ",
                    projector.Consumers.Select(c => $"{c.Topic}: processed {c.Processed}, last caught up {c.LastCaughtUpUtc:O}")));
                await Task.Delay(100);
            }
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await run;
            }
            catch (OperationCanceledException)
            {
                // Stopping a handler mid-backoff is how the run ends; the offset was not committed.
            }
        }
    }

    public static async Task<string> DumpAsync(NpgsqlDataSource db)
    {
        await using var conn = await db.OpenConnectionAsync();
        return await CanonicalDump.DumpSchemaAsync(conn, "operations");
    }

    public static async Task<TopicPartitionOffset> ProduceAsync(IProducer<string, byte[]> producer, string topic, string key, JsonNode? value)
    {
        var result = await producer.ProduceAsync(topic, new Message<string, byte[]>
        {
            Key = key,
            Value = value is null ? null! : Encoding.UTF8.GetBytes(value.ToJsonString()),
        });
        return result.TopicPartitionOffset;
    }

    /// <summary>A contract example, as a node to change before producing it.</summary>
    public static JsonObject Example(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "contracts", "examples", name + ".json")))!.AsObject();

    /// <summary>An equipment id the schema accepts and no other test uses.</summary>
    public static string NewEquipment() => "eq-" + Random.Shared.Next(100_000, 999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string NewId() => Guid.NewGuid().ToString();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "contracts", "jsonschema", "v1")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("could not find the repository root");
    }
}
