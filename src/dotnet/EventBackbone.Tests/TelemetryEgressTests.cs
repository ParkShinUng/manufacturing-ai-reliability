using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using Mair.EdgeGateway;
using Sim = Mair.EquipmentSimulator;

namespace Mair.EventBackbone.Tests;

/// <summary>
/// Phase 3 step 4, telemetry half: the gateway's egress port bound to <c>factory.telemetry.v1</c>
/// (FR-010).
/// <para>
/// The claim is end to end: what the gateway writes is a record the contract accepts, and the
/// production consume path reads it back unchanged. Both halves are checked against the
/// authoritative schema rather than against a copy of it written in C#.
/// </para>
/// </summary>
[Collection(SharedKafka.Name)]
public sealed class TelemetryEgressTests : IAsyncLifetime
{
    private static readonly string Root = FindRoot();

    private readonly KafkaBroker _broker;

    public TelemetryEgressTests(KafkaBroker broker) => _broker = broker;

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

    private static ContractSchema Schema() =>
        ContractSchema.Load(Path.Combine(Root, "contracts", "jsonschema", "v1", "telemetry.schema.json"));

    public async Task InitializeAsync()
    {
        using var admin = _broker.Admin();
        await new TopicBootstrap(admin).RunAsync(TopicRegister.All);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Real records, not hand-built ones: the simulator through the Modbus encoder and the gateway's
    /// decoder and normaliser, over profiles that produce healthy readings, dead sensors, noise,
    /// degradation and faults — so the flags, nulls and states the serialiser meets are the ones
    /// production meets.
    /// </summary>
    private static IEnumerable<CanonicalTelemetry> Records(int equipment, int ticks)
    {
        Sim.FaultProfile[] profiles =
        [
            Sim.FaultProfile.Normal, Sim.FaultProfile.BearingDegradation, Sim.FaultProfile.SensorDropout,
            Sim.FaultProfile.CoolingDegradation, Sim.FaultProfile.SensorNoise, Sim.FaultProfile.SensorFreeze,
            Sim.FaultProfile.Overload, Sim.FaultProfile.OodProfile,
        ];

        for (var i = 0; i < equipment; i++)
        {
            var profile = profiles[i % profiles.Length];
            var sim = new Sim.EquipmentSimulation(new Sim.EquipmentOptions
            {
                EquipmentId = $"eq-{i + 1:D3}",
                Seed = (ulong)(20260929 + i),
                FaultProfile = profile,
                FaultChannel = profile is Sim.FaultProfile.SensorDropout or Sim.FaultProfile.SensorNoise or Sim.FaultProfile.SensorFreeze
                    ? Sim.SensorChannel.TorqueNm
                    : null,
            });
            sim.Connect();

            var normaliser = new TelemetryNormaliser($"eq-{i + 1:D3}", "edge-gateway@phase3");
            for (var t = 0; t < ticks; t++)
            {
                sim.WriteSetpoint(100);
                if (sim.Tick() is { } sample)
                {
                    yield return normaliser.Normalise(
                        ModbusRegisterDecoder.Decode(Sim.ModbusRegisterEncoder.Encode(sample)));
                }
            }
        }
    }

    [Fact]
    public void EverySerialisedRecordIsValidAgainstTheContract()
    {
        var schema = Schema();
        var checkedCount = 0;
        var flagged = 0;
        var withNull = 0;

        foreach (var record in Records(equipment: 16, ticks: 900))
        {
            var verdict = schema.Validate(TelemetryJson.Serialize(record));
            Assert.True(verdict.Valid, $"{record.EquipmentId} #{record.Sequence}: {verdict.Reason}");

            checkedCount++;
            flagged += record.Flags.Count > 0 ? 1 : 0;
            withNull += record.TorqueNm is null ? 1 : 0;
        }

        // Guard against a vacuous pass: the run must actually have met flags and dead sensors.
        Assert.Equal(16 * 900, checkedCount);
        Assert.True(flagged > 0, "no record carried a flag, so flag serialisation was never exercised");
        Assert.True(withNull > 0, "no record carried a null measurement");
    }

    [Fact]
    public async Task WhatTheGatewayProducesIsReadBackUnchangedThroughTheProductionConsumePath()
    {
        var records = Records(equipment: 4, ticks: 50).ToList();
        var ids = records.Select(r => r.EquipmentId).ToHashSet();

        using (var sink = new KafkaTelemetrySink(_broker.BootstrapServers))
        {
            foreach (var r in records)
            {
                sink.Emit(r);
            }

            sink.Flush(TimeSpan.FromSeconds(30));
            Assert.Equal(records.Count, sink.Delivered);
            Assert.Equal(0, sink.DeliveryFailures);
        }

        var received = new List<ConsumeResult<string, byte[]>>();
        using var deadLetters = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = _broker.BootstrapServers }).Build();
        using (var consumer = new ContractConsumer(
                   new ConsumerConfig { BootstrapServers = _broker.BootstrapServers, GroupId = "egress-" + Guid.NewGuid().ToString("N"), AutoOffsetReset = AutoOffsetReset.Earliest },
                   KafkaTelemetrySink.Topic, Schema(), deadLetters,
                   (r, _) => { if (ids.Contains(r.Message.Key)) { received.Add(r); } return Task.CompletedTask; }))
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (received.Count < records.Count)
            {
                Assert.True(DateTime.UtcNow < deadline, $"received {received.Count} of {records.Count}");
                await consumer.ProcessOneAsync(TimeSpan.FromSeconds(1));
            }

            // Every record passed the contract on the way in: none were dead-lettered.
            Assert.Equal(0, consumer.DeadLettered);
        }

        // Byte-for-byte what was produced, keyed by equipment.
        var produced = records.ToDictionary(r => (r.EquipmentId, r.Sequence), TelemetryJson.Serialize);
        foreach (var r in received)
        {
            var sequence = ulong.Parse(System.Text.Json.JsonDocument.Parse(r.Message.Value).RootElement.GetProperty("sequence"u8).GetRawText());
            Assert.Equal(produced[(r.Message.Key, sequence)], r.Message.Value);
        }

        // Per-key order survives the trip: one equipment's sequences arrive increasing (§4).
        foreach (var group in received.GroupBy(r => r.Message.Key))
        {
            var sequences = group.Select(r => System.Text.Json.JsonDocument.Parse(r.Message.Value).RootElement.GetProperty("sequence"u8).GetUInt64()).ToList();
            Assert.Equal(sequences.Order().ToList(), sequences);
        }
    }

    [Fact]
    public void EmitDoesNotBlockWhenTheBrokerIsUnreachable()
    {
        // §4: Kafka is non-control-critical and never blocks the OT poll loop. With nothing
        // listening, Emit must still return at once; delivery failure is a later, counted event.
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var deadPort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var records = Records(equipment: 1, ticks: 200).ToList();
        using var sink = new KafkaTelemetrySink($"localhost:{deadPort}");

        var started = Stopwatch.GetTimestamp();
        foreach (var r in records)
        {
            sink.Emit(r);
        }

        var elapsed = Stopwatch.GetElapsedTime(started);

        // 200 records against a 100 ms poll period: anything near that period would be blocking.
        Assert.True(elapsed < TimeSpan.FromMilliseconds(500), $"200 emits took {elapsed.TotalMilliseconds:F0} ms");
        Assert.Equal(0, sink.Delivered);
    }
}
