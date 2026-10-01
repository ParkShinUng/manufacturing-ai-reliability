using System.Text.Json.Nodes;
using Confluent.Kafka;
using Mair.EventBackbone;
using Mair.OperationsService.Projection;
using Npgsql;

namespace Mair.OperationsService.Tests;

/// <summary>
/// <b>PROJ-001 / AC-046</b> — replaying a known offset range into the <b>registered</b> projector
/// group rebuilds the read models byte-identically, replaces only the rows of that range, and does
/// it again identically the second time (OD-014).
/// <para>
/// A rig of its own: <see cref="ReplayTool"/> only rewinds groups the register makes
/// replay-eligible, so this class uses <c>cg.operations-projector.v1</c> itself, and no other class
/// may share its broker.
/// </para>
/// </summary>
public sealed class ProjectionRebuildTests(ProjectionRig rig) : IClassFixture<ProjectionRig>
{
    private static JsonObject Telemetry(string equipment, int second, long sequence)
    {
        var t = ProjectionRig.Example("telemetry.healthy");
        var at = new DateTime(2026, 10, 1, 6, 0, second, 250, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        t["eventId"] = ProjectionRig.NewId();
        t["equipmentId"] = equipment;
        t["eventTimeUtc"] = at;
        t["occurredAtUtc"] = at;
        t["sequence"] = sequence;
        t["correlationId"] = ProjectionRig.NewId();
        return t;
    }

    private static async Task<long> CountAsync(NpgsqlDataSource db, string sql, params object[] args)
    {
        await using var cmd = db.CreateCommand(sql);
        foreach (var arg in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task ARangeReplayRebuildsByteIdentically_TouchesOnlyItsRange_AndIsIdempotent()
    {
        using var producer = rig.Producer();
        var eq = ProjectionRig.NewEquipment();
        var offsets = new List<TopicPartitionOffset>();
        for (var i = 0; i < 10; i++)
        {
            offsets.Add(await ProjectionRig.ProduceAsync(producer, "factory.telemetry.v1", eq, Telemetry(eq, i, 100 + i)));
        }

        await using var db = await rig.MigratedAsync();
        using (var projector = rig.Projector(db, producer, ConsumerGroupRegister.OperationsProjector))
        {
            await ProjectionRig.CatchUpAsync(projector);
        }

        var original = await ProjectionRig.DumpAsync(db);
        var from = offsets[4];
        // This equipment's rows only: the class's other test writes to the same topic and partition.
        const string before = "SELECT count(*) FROM operations.telemetry_reading_1s WHERE equipment_id = $3 AND source_partition = $1 AND source_offset < $2";
        var untouched = await CountAsync(db, before, from.Partition.Value, from.Offset.Value, eq);
        Assert.Equal(4, untouched);

        for (var replay = 1; replay <= 2; replay++)
        {
            using (var admin = rig.Kafka.Admin())
            {
                // Exactly the rows at or after the offset, whichever equipment they belong to - six of
                // them this test's; the four before it are not touched.
                var inRange = await CountAsync(db,
                    "SELECT count(*) FROM operations.telemetry_reading_1s WHERE source_partition = $1 AND source_offset >= $2",
                    from.Partition.Value, from.Offset.Value);
                Assert.True(inRange >= 6);
                Assert.Equal(inRange, await ProjectionRebuild.ReplaceFromAsync(admin, db, ConsumerGroupRegister.OperationsProjector, [from]));
            }

            Assert.Equal(untouched, await CountAsync(db, before, from.Partition.Value, from.Offset.Value, eq));
            Assert.Equal(4, await CountAsync(db, "SELECT count(*) FROM operations.telemetry_reading_1s WHERE equipment_id = $1", eq));

            using (var projector = rig.Projector(db, producer, ConsumerGroupRegister.OperationsProjector))
            {
                await ProjectionRig.CatchUpAsync(projector);
            }

            Assert.Equal(original, await ProjectionRig.DumpAsync(db));
        }
    }

    [Fact]
    public async Task TheSupervisorsGroupCannotBeUsedToRebuild_AndNothingIsDeleted()
    {
        using var producer = rig.Producer();
        var eq = ProjectionRig.NewEquipment();
        var at = await ProjectionRig.ProduceAsync(producer, "factory.telemetry.v1", eq, Telemetry(eq, 30, 1));

        await using var db = await rig.MigratedAsync();
        using (var projector = rig.Projector(db, producer))
        {
            await ProjectionRig.CatchUpAsync(projector);
        }

        var dump = await ProjectionRig.DumpAsync(db);
        using var admin = rig.Kafka.Admin();
        await Assert.ThrowsAsync<ReplayRefusedException>(() =>
            ProjectionRebuild.ReplaceFromAsync(admin, db, ConsumerGroupRegister.SafetySupervisor, [at]));

        Assert.Equal(dump, await ProjectionRig.DumpAsync(db));
    }
}
