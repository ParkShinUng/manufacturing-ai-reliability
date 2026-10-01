using System.Text.Json.Nodes;
using Confluent.Kafka;
using Npgsql;

namespace Mair.OperationsService.Tests;

/// <summary>
/// <b>PROJ-002 / AC-028</b> and the storage half of <b>AUDIT-001 / AC-029</b>: the projector on the
/// register's real topics, through the shared consumer, into a real PostgreSQL.
/// <para>
/// Predictions, decisions and outcomes have no producer before Phases 5–7, so they are contract
/// examples with new identities, validated by the shared consumer like any record (OD-011). This
/// proves storage, rebuild and query — not that the later producers link up.
/// </para>
/// </summary>
public sealed class ProjectionTests(ProjectionRig rig) : IClassFixture<ProjectionRig>
{
    private static JsonObject Telemetry(string equipment, string eventTime, long sequence, double temperature)
    {
        var t = ProjectionRig.Example("telemetry.healthy");
        t["eventId"] = ProjectionRig.NewId();
        t["equipmentId"] = equipment;
        t["eventTimeUtc"] = eventTime;
        t["occurredAtUtc"] = eventTime;
        t["sequence"] = sequence;
        t["correlationId"] = ProjectionRig.NewId();
        t["measurements"]!["temperatureC"] = temperature;
        return t;
    }

    private static JsonObject State(string equipment, long sequence, string state, string at)
    {
        var s = ProjectionRig.Example("equipment-state");
        s["eventId"] = ProjectionRig.NewId();
        s["equipmentId"] = equipment;
        s["stateSequence"] = sequence;
        s["state"] = state;
        s["occurredAtUtc"] = at;
        s["correlationId"] = ProjectionRig.NewId();
        return s;
    }

    private static async Task<object?> ScalarAsync(NpgsqlDataSource db, string sql, params object[] args)
    {
        await using var cmd = db.CreateCommand(sql);
        foreach (var arg in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        return await cmd.ExecuteScalarAsync();
    }

    /// <summary>A stream over every projected topic, with the awkward cases PROJ-002 names.</summary>
    private static async Task<string> ProduceStreamAsync(IProducer<string, byte[]> producer)
    {
        var eq = ProjectionRig.NewEquipment();

        // Second 04:00:00 - a duplicate delivery, then a record with the same event time and a
        // higher sequence, which must win.
        var first = Telemetry(eq, "2026-10-01T04:00:00.100Z", 10, 40.0);
        await ProjectionRig.ProduceAsync(producer, "factory.telemetry.v1", eq, first);
        await ProjectionRig.ProduceAsync(producer, "factory.telemetry.v1", eq, first);
        await ProjectionRig.ProduceAsync(producer, "factory.telemetry.v1", eq, Telemetry(eq, "2026-10-01T04:00:00.100Z", 11, 41.0));

        // Second 04:00:01, then a late record for 04:00:00 that is older than the winner: no change.
        await ProjectionRig.ProduceAsync(producer, "factory.telemetry.v1", eq, Telemetry(eq, "2026-10-01T04:00:01.900Z", 30, 43.0));
        await ProjectionRig.ProduceAsync(producer, "factory.telemetry.v1", eq, Telemetry(eq, "2026-10-01T04:00:00.050Z", 9, 39.0));

        await ProjectionRig.ProduceAsync(producer, "factory.equipment-states.v1", eq, State(eq, 1, "RUNNING", "2026-10-01T04:00:00.000Z"));

        var prediction = ProjectionRig.Example("prediction");
        prediction["predictionId"] = ProjectionRig.NewId();
        prediction["equipmentId"] = eq;
        prediction["correlationId"] = ProjectionRig.NewId();
        await ProjectionRig.ProduceAsync(producer, "factory.predictions.v1", eq, prediction);

        var decision = ProjectionRig.Example("safety-decision.clamped");
        decision["decisionId"] = ProjectionRig.NewId();
        decision["equipmentId"] = eq;
        decision["predictionId"] = prediction["predictionId"]!.GetValue<string>();
        decision["causationId"] = prediction["predictionId"]!.GetValue<string>();
        decision["correlationId"] = prediction["correlationId"]!.GetValue<string>();
        await ProjectionRig.ProduceAsync(producer, "factory.safety-decisions.v1", eq, decision);

        var outcome = ProjectionRig.Example("control-outcome.watchdog");
        outcome["commandId"] = ProjectionRig.NewId();
        outcome["equipmentId"] = eq;
        await ProjectionRig.ProduceAsync(producer, "factory.control-outcomes.v1", eq, outcome);

        await ProjectionRig.ProduceAsync(producer, "factory.model-deployments.v1", "bearing-health", ProjectionRig.Example("model-authorization.quarantine"));
        await ProjectionRig.ProduceAsync(producer, "factory.model-deployments.v1", "__watermark__", ProjectionRig.Example("model-authorization.watermark"));
        return eq;
    }

    [Fact]
    public async Task ARebuildIntoAnEmptyStoreEqualsTheOriginal_ByteForByte()
    {
        using var producer = rig.Producer();
        var eq = await ProduceStreamAsync(producer);

        await using var original = await rig.MigratedAsync();
        using (var projector = rig.Projector(original, producer))
        {
            await ProjectionRig.CatchUpAsync(projector);
        }

        await using var rebuilt = await rig.MigratedAsync();
        using (var projector = rig.Projector(rebuilt, producer))
        {
            await ProjectionRig.CatchUpAsync(projector);
        }

        Assert.Equal(await ProjectionRig.DumpAsync(original), await ProjectionRig.DumpAsync(rebuilt));

        // OD-015: per second, the reading with the greatest (eventTimeUtc, sequence).
        Assert.Equal(41.0, await ScalarAsync(original,
            "SELECT temperature_c FROM operations.telemetry_reading_1s WHERE equipment_id = $1 AND second_utc = '2026-10-01T04:00:00Z'", eq));
        Assert.Equal(11L, await ScalarAsync(original,
            "SELECT sequence FROM operations.telemetry_reading_1s WHERE equipment_id = $1 AND second_utc = '2026-10-01T04:00:00Z'", eq));
        Assert.Equal(2L, await ScalarAsync(original, "SELECT count(*) FROM operations.telemetry_reading_1s WHERE equipment_id = $1", eq));
    }

    [Fact]
    public async Task ATombstoneKeepsHistory_RemovesTheEquipmentFromTheCurrentView_AndALaterStateRestoresIt()
    {
        using var producer = rig.Producer();
        var eq = ProjectionRig.NewEquipment();
        await ProjectionRig.ProduceAsync(producer, "factory.equipment-states.v1", eq, State(eq, 1, "RUNNING", "2026-10-01T05:00:00.000Z"));
        await ProjectionRig.ProduceAsync(producer, "factory.equipment-states.v1", eq, null);

        await using var db = await rig.MigratedAsync();
        using (var projector = rig.Projector(db, producer))
        {
            await ProjectionRig.CatchUpAsync(projector);
            // Only this topic: the others carry what other tests produced, including deliberate rejects.
            Assert.Equal(0, projector.Consumers.Single(c => c.Topic == "factory.equipment-states.v1").DeadLettered);
        }

        Assert.Equal(1L, await ScalarAsync(db, "SELECT count(*) FROM operations.equipment_state_history WHERE equipment_id = $1", eq));
        Assert.Equal(1L, await ScalarAsync(db, "SELECT count(*) FROM operations.equipment_decommission WHERE equipment_id = $1", eq));
        Assert.Equal(0L, await ScalarAsync(db, "SELECT count(*) FROM operations.current_equipment_state WHERE equipment_id = $1", eq));

        // It reappears: current again.
        await ProjectionRig.ProduceAsync(producer, "factory.equipment-states.v1", eq, State(eq, 2, "IDLE", "2026-10-01T05:10:00.000Z"));
        var rebuiltDump = "";
        using (var projector = rig.Projector(db, producer))
        {
            await ProjectionRig.CatchUpAsync(projector);
            // A redelivered tombstone (a fresh group reads it again) changes nothing.
            Assert.Equal(1L, await ScalarAsync(db, "SELECT count(*) FROM operations.equipment_decommission WHERE equipment_id = $1", eq));
        }

        Assert.Equal("IDLE", await ScalarAsync(db, "SELECT state FROM operations.current_equipment_state WHERE equipment_id = $1", eq));

        // The same retained range into a fresh store: the same dump, and the same current view.
        await using var fresh = await rig.MigratedAsync();
        using (var projector = rig.Projector(fresh, producer))
        {
            await ProjectionRig.CatchUpAsync(projector);
        }

        rebuiltDump = await ProjectionRig.DumpAsync(fresh);
        Assert.Equal(await ProjectionRig.DumpAsync(db), rebuiltDump);
        Assert.Equal("IDLE", await ScalarAsync(fresh, "SELECT state FROM operations.current_equipment_state WHERE equipment_id = $1", eq));
    }

    [Fact]
    public async Task AModeTransitionIsStoredWithItsFromMode_AndOneWithoutIsDeadLettered()
    {
        using var producer = rig.Producer();
        var eq = ProjectionRig.NewEquipment();

        var transition = ProjectionRig.Example("control-outcome.watchdog");
        transition["commandId"] = ProjectionRig.NewId();
        transition["equipmentId"] = eq;
        await ProjectionRig.ProduceAsync(producer, "factory.control-outcomes.v1", eq, transition);

        var broken = transition.DeepClone().AsObject();
        broken["commandId"] = ProjectionRig.NewId();
        broken.Remove("fromMode");
        await ProjectionRig.ProduceAsync(producer, "factory.control-outcomes.v1", eq, broken);

        await using var db = await rig.MigratedAsync();
        using var projector = rig.Projector(db, producer);
        await ProjectionRig.CatchUpAsync(projector);

        Assert.Equal("AI_ASSISTED", await ScalarAsync(db,
            "SELECT from_mode FROM operations.control_outcome WHERE command_id = $1::uuid", transition["commandId"]!.GetValue<string>()));
        Assert.Equal("M4", await ScalarAsync(db,
            "SELECT mode_transition_id FROM operations.control_outcome WHERE command_id = $1::uuid", transition["commandId"]!.GetValue<string>()));
        Assert.Equal(1L, await ScalarAsync(db, "SELECT count(*) FROM operations.control_outcome WHERE equipment_id = $1", eq));
        Assert.True(projector.Consumers.Single(c => c.Topic == "factory.control-outcomes.v1").DeadLettered >= 1);
    }
}
