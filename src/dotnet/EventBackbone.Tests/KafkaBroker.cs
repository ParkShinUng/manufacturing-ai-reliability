using System.Net;
using System.Net.Sockets;
using Confluent.Kafka;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Mair.EventBackbone.Tests;

/// <summary>
/// A broker for one test class, on the image <c>ADR-0021</c> pins — tag <b>and</b> digest.
/// <para>
/// A container per test class, not per run: <c>AC-026</c>'s "create from nothing, then run again
/// unchanged" is only meaningful against a broker with no topics on it.
/// </para>
/// <para>
/// <b>Built from the Testcontainers core rather than its Kafka module</b> (ADR-0021, amended
/// 2026-09-29). <c>Testcontainers.Kafka</c> 4.15.0 cannot start this image: it writes the
/// advertised listeners into a startup script after the container is up, while the Apache image
/// formats its storage in its entrypoint first and dies with <c>'advertised.listeners' values must
/// not be empty</c>. Reproduced with and without the digest; the same image starts correctly from a
/// plain <c>docker run</c> with the variables below. The module's default image is
/// <c>confluentinc/cp-kafka</c>, so deferring to it would have meant testing a different
/// distribution from the one <c>deploy/compose/</c> runs, which driver 4 of the ADR forbids.
/// </para>
/// </summary>
public sealed class KafkaBroker : IAsyncLifetime
{
    public const string Image =
        "apache/kafka:4.3.1@sha256:77e3df9054047a88b520d0cc46e16696d3b22022e1d580aeccd2632df6532837";

    private readonly int _port = FreePort();
    private readonly IContainer _container;

    public KafkaBroker()
    {
        // The advertised listener must be correct at start-up, because that is when the image
        // formats its storage. Binding a port chosen here means the address is known before the
        // container runs; a Docker-assigned random port would not be known until after.
        _container = new ContainerBuilder(Image)
            .WithPortBinding(_port, 9092)
            .WithEnvironment("KAFKA_NODE_ID", "1")
            .WithEnvironment("KAFKA_PROCESS_ROLES", "broker,controller")
            .WithEnvironment("KAFKA_LISTENERS", "PLAINTEXT://:9092,CONTROLLER://:9093")
            .WithEnvironment("KAFKA_ADVERTISED_LISTENERS", $"PLAINTEXT://localhost:{_port}")
            .WithEnvironment("KAFKA_LISTENER_SECURITY_PROTOCOL_MAP", "PLAINTEXT:PLAINTEXT,CONTROLLER:PLAINTEXT")
            .WithEnvironment("KAFKA_CONTROLLER_LISTENER_NAMES", "CONTROLLER")
            .WithEnvironment("KAFKA_CONTROLLER_QUORUM_VOTERS", "1@localhost:9093")

            // One broker cannot host three replicas, and these internal topics default to more.
            // RF 1 locally is the register's own local column (§2).
            .WithEnvironment("KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR", "1")
            .WithEnvironment("KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR", "1")
            .WithEnvironment("KAFKA_TRANSACTION_STATE_LOG_MIN_ISR", "1")
            .WithEnvironment("KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS", "0")

            // Auto-creation off: §2's register is the only thing that may create a topic, and a
            // producer-created one would take the broker's default partition count.
            .WithEnvironment("KAFKA_AUTO_CREATE_TOPICS_ENABLE", "false")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Kafka Server started"))
            .Build();
    }

    public string BootstrapServers => $"localhost:{_port}";

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public IAdminClient Admin() =>
        new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
