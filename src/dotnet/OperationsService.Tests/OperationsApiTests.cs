using Mair.OperationsService.Api;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Mair.OperationsService.Tests;

/// <summary>
/// <b>API-001 / AC-030</b>, <b>TRACE-001 / AC-047</b> and the query half of <b>AUDIT-001 / AC-029</b>,
/// against the API on Kestrel over a projected store. Every response passes through the ADR-0024
/// harness; one nonconforming response fails the test that received it.
/// <para>
/// Predictions, decisions and outcomes are contract examples with new identities (OD-011): this proves
/// storage and query, not that the later producers link up.
/// </para>
/// </summary>
public sealed class OperationsApiTests(ApiRig rig) : IClassFixture<ApiRig>
{
    private const string Api = "/api/v1";

    /// <summary>The status, with the body in the message when it is not the one expected.</summary>
    private async Task ExpectAsync(int status, string path, string? token)
    {
        var (actual, _, body) = await rig.GetAsync(path, token);
        Assert.True(actual == status, $"{path}: expected {status}, got {actual}: {body}");
    }

    private static string At(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private sealed record Seeded(string Equipment, string Bare, string Correlation, DateTime WindowStart);

    /// <summary>
    /// One equipment with a full chain under correlation ID <c>C</c> (a REJECT, so its reason is
    /// active), and one with a state record and nothing else.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        var eq = ProjectionRig.NewEquipment();
        var bare = ProjectionRig.NewEquipment();
        var correlation = ProjectionRig.NewId();
        var p = rig.Producer;

        foreach (var (e, seq) in new[] { (eq, 1L), (bare, 1L) })
        {
            var s = ProjectionRig.Example("equipment-state");
            s["eventId"] = ProjectionRig.NewId();
            s["equipmentId"] = e;
            s["stateSequence"] = seq;
            s["state"] = "RUNNING";
            s["correlationId"] = ProjectionRig.NewId();
            await ProjectionRig.ProduceAsync(p, "factory.equipment-states.v1", e, s);
        }

        var prediction = ProjectionRig.Example("prediction");
        prediction["predictionId"] = ProjectionRig.NewId();
        prediction["equipmentId"] = eq;
        prediction["correlationId"] = correlation;
        var windowStart = DateTime.Parse(prediction["featureWindow"]!["startUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        await ProjectionRig.ProduceAsync(p, "factory.predictions.v1", eq, prediction);

        // Two readings inside the window, one just before it.
        foreach (var (offsetSeconds, seq) in new[] { (5, 10L), (30, 11L), (-1, 9L) })
        {
            var t = ProjectionRig.Example("telemetry.healthy");
            var at = At(windowStart.AddSeconds(offsetSeconds).AddMilliseconds(100));
            t["eventId"] = ProjectionRig.NewId();
            t["equipmentId"] = eq;
            t["eventTimeUtc"] = at;
            t["occurredAtUtc"] = at;
            t["sequence"] = seq;
            t["correlationId"] = ProjectionRig.NewId();
            await ProjectionRig.ProduceAsync(p, "factory.telemetry.v1", eq, t);
        }

        var decision = ProjectionRig.Example("safety-decision.clamped");
        decision["decisionId"] = ProjectionRig.NewId();
        decision["equipmentId"] = eq;
        decision["predictionId"] = prediction["predictionId"]!.GetValue<string>();
        decision["causationId"] = prediction["predictionId"]!.GetValue<string>();
        decision["correlationId"] = correlation;
        decision["decision"] = "REJECT";
        decision["reasonCodes"] = new JsonArray("OOD_HIGH");
        await ProjectionRig.ProduceAsync(p, "factory.safety-decisions.v1", eq, decision);

        var outcome = ProjectionRig.Example("control-outcome.watchdog");
        outcome["commandId"] = ProjectionRig.NewId();
        outcome["equipmentId"] = eq;
        outcome["decisionId"] = decision["decisionId"]!.GetValue<string>();
        outcome["causationId"] = decision["decisionId"]!.GetValue<string>();
        outcome["correlationId"] = correlation;
        await ProjectionRig.ProduceAsync(p, "factory.control-outcomes.v1", eq, outcome);

        await ProjectionRig.ProduceAsync(p, "factory.model-deployments.v1", "bearing-health", ProjectionRig.Example("model-authorization.quarantine"));
        await ProjectionRig.ProduceAsync(p, "factory.model-deployments.v1", "__watermark__", ProjectionRig.Example("model-authorization.watermark"));

        await rig.CaughtUpAsync();
        return new Seeded(eq, bare, correlation, windowStart);
    }

    [Fact]
    public async Task EveryDocumentedResponseOfEveryRouteIsProducedAndConforms()
    {
        var seeded = await SeedAsync();
        var viewer = rig.Mint("api001", "viewer");
        var noRole = rig.Mint("api001-norole", "nobody");
        var unknown = "eq-999" + Random.Shared.Next(100, 999).ToString(CultureInfo.InvariantCulture);

        string[] routes =
        [
            $"/equipment", $"/equipment/{seeded.Equipment}", $"/equipment/{seeded.Equipment}/decisions",
            $"/equipment/{seeded.Equipment}/commands", $"/trace/{seeded.Correlation}", "/platform/health", "/models",
        ];

        foreach (var route in routes)
        {
            await ExpectAsync(200, Api + route, viewer);
            await ExpectAsync(401, Api + route, null);
            await ExpectAsync(403, Api + route, noRole);

            // A burst above 40 from one subject: a subject of its own, so no other call is starved.
            var burst = rig.Mint("burst" + route.GetHashCode(StringComparison.Ordinal), "viewer");
            var statuses = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => rig.GetAsync(Api + route, burst)));
            Assert.Contains(statuses, r => r.Status == 429);
        }

        // 400: one malformed parameter per route that takes any.
        foreach (var bad in new[]
                 {
                     "/equipment?limit=0", "/equipment?cursor=not-a-cursor", "/equipment?state=SLEEPING", "/equipment/eq-1",
                     $"/equipment/{seeded.Equipment}/decisions?limit=501", $"/equipment/{seeded.Equipment}/decisions?from=yesterday",
                     $"/equipment/{seeded.Equipment}/commands?cursor=e30", "/trace/not-a-uuid",
                 })
        {
            await ExpectAsync(400, Api + bad, viewer);
        }

        // 404.
        foreach (var missing in new[] { $"/equipment/{unknown}", $"/equipment/{unknown}/decisions", $"/equipment/{unknown}/commands", $"/trace/{Guid.NewGuid()}" })
        {
            await ExpectAsync(404, Api + missing, viewer);
        }

        // OD-012: an equipment with no outcome, reading or prediction - null, never a guess.
        using (var bare = JsonDocument.Parse((await rig.GetAsync($"{Api}/equipment/{seeded.Bare}", viewer)).Body))
        {
            Assert.Equal(JsonValueKind.Null, bare.RootElement.GetProperty("controlMode"u8).ValueKind);
            Assert.Equal(JsonValueKind.Null, bare.RootElement.GetProperty("qualityOverall"u8).ValueKind);
            Assert.Equal(JsonValueKind.Null, bare.RootElement.GetProperty("controlEpoch"u8).ValueKind);
        }

        // FR-050: the REJECT's reason is active; the staleness header reads 0 while caught up.
        // A caught-up check that fails - under the burst above, a watermark query can time out -
        // rightly makes the header grow. What is asserted here is the mapping: freshly confirmed
        // caught up, the header reads 0. Failed once without this wait; cause not proven.
        await rig.CaughtUpAsync();
        var detail = await rig.GetAsync($"{Api}/equipment/{seeded.Equipment}", viewer);
        using (var full = JsonDocument.Parse(detail.Body))
        {
            Assert.Equal(["OOD_HIGH"], full.RootElement.GetProperty("activeReasonCodes"u8).EnumerateArray().Select(c => c.GetString()));
            Assert.Equal("SAFE_FALLBACK", full.RootElement.GetProperty("controlMode"u8).GetString());
        }

        Assert.True(detail.Response.Headers.GetValues("X-Data-Staleness-Seconds").Single() == "0", string.Join(" | ",
            rig.Projector.Consumers.Select(c => $"{c.Topic}: caughtUp={c.CaughtUp} last={c.LastCaughtUpUtc:O} outage={c.Outage} processed={c.Processed}")));

        using (var health = JsonDocument.Parse((await rig.GetAsync($"{Api}/platform/health", viewer)).Body))
        {
            Assert.Equal(JsonValueKind.Null, health.RootElement.GetProperty("supervisorHealthy"u8).ValueKind);
            Assert.True(health.RootElement.GetProperty("kafkaReachable"u8).GetBoolean());
        }

        // 503: PostgreSQL stopped. Health still answers - without the field only the store can give.
        await rig.Infra.Postgres.StopAsync();
        try
        {
            foreach (var route in routes.Where(r => r != "/platform/health"))
            {
                await ExpectAsync(503, Api + route, viewer);
            }

            using var health = JsonDocument.Parse((await rig.GetAsync($"{Api}/platform/health", viewer)).Body);
            Assert.False(health.RootElement.TryGetProperty("fallbackRatePct"u8, out _));
        }
        finally
        {
            await rig.RestartPostgresAsync();
            await rig.BreakerClosedAsync(viewer);
        }

        // The demo route is absent outside the demo profile (Phase 11): not part of the contract run here.
        using (var demo = await rig.Http.PostAsync($"{Api}/demo/faults", new StringContent("{}")))
        {
            Assert.Equal(404, (int)demo.StatusCode);
        }

        Assert.Empty(rig.Contract.Uncovered(d => d.Template == "/demo/faults"));
        Assert.Empty(rig.Failures);
    }

    [Fact]
    public async Task FailDb001_AStoreOutageLosesNothing_TheBreakerOpens_AndHealthStillAnswers()
    {
        await SeedAsync();
        var viewer = rig.Mint("faildb001", "viewer");
        var eq = ProjectionRig.NewEquipment();
        var telemetry = rig.Projector.Consumers.Single(c => c.Topic == "factory.telemetry.v1");
        var deadLetteredBefore = telemetry.DeadLettered;

        await rig.Infra.Postgres.StopAsync();
        try
        {
            // Five readings produced while the store is down.
            for (var i = 0; i < 5; i++)
            {
                var t = ProjectionRig.Example("telemetry.healthy");
                var at = At(new DateTime(2026, 10, 2, 1, 0, i, 100, DateTimeKind.Utc));
                t["eventId"] = ProjectionRig.NewId();
                t["equipmentId"] = eq;
                t["eventTimeUtc"] = at;
                t["occurredAtUtc"] = at;
                t["sequence"] = i;
                t["correlationId"] = ProjectionRig.NewId();
                await ProjectionRig.ProduceAsync(rig.Producer, "factory.telemetry.v1", eq, t);
            }

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (telemetry.Outage is null)
            {
                Assert.True(DateTime.UtcNow < deadline, "the projector never reported the outage");
                await Task.Delay(100);
            }

            // Five unavailable answers open the breaker; the sixth is refused without asking the store.
            for (var i = 0; i < CircuitBreaker.Threshold; i++)
            {
                await ExpectAsync(503, $"{Api}/equipment/{eq}/decisions", viewer);
            }

            var refused = await rig.GetAsync($"{Api}/equipment/{eq}/decisions", viewer);
            Assert.Equal(503, refused.Status);
            Assert.Contains("circuit open", refused.Body);

            Assert.Equal(200, (await rig.GetAsync($"{Api}/platform/health", viewer)).Status);
        }
        finally
        {
            await rig.RestartPostgresAsync();
        }

        await rig.CaughtUpAsync();
        Assert.Null(telemetry.Outage);
        Assert.Equal(deadLetteredBefore, telemetry.DeadLettered);
        await using (var count = rig.Db.CreateCommand("SELECT count(*) FROM operations.telemetry_reading_1s WHERE equipment_id = $1"))
        {
            count.Parameters.Add(new NpgsqlParameter { Value = eq });
            Assert.Equal(5L, await count.ExecuteScalarAsync());
        }

        // The read models after the outage equal a rebuild of the same topics into an empty store.
        await using var fresh = await rig.Infra.MigratedAsync();
        using (var projector = rig.Infra.Projector(fresh, rig.Producer))
        {
            await ProjectionRig.CatchUpAsync(projector);
        }

        // Only what came from Kafka: TRACE-001 wrote readings straight to the table (provenance 'seed'),
        // which no replay can - or should - reproduce.
        static string FromKafka(string dump) => string.Join('\n', dump.Split('\n').Where(l => !l.Contains("\tseed\t", StringComparison.Ordinal)));
        Assert.Equal(FromKafka(await ProjectionRig.DumpAsync(fresh)), FromKafka(await ProjectionRig.DumpAsync(rig.Db)));
        await rig.BreakerClosedAsync(viewer);
    }

    [Fact]
    public async Task TheStalenessHeaderGrowsWhileTheBrokerIsDown_AndReturnsToZero()
    {
        await SeedAsync();
        var viewer = rig.Mint("staleness", "viewer");

        var restart = rig.Infra.Kafka.RestartAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromSeconds(6));
        var during = await rig.GetAsync($"{Api}/models", viewer);
        Assert.True(double.Parse(during.Response.Headers.GetValues("X-Data-Staleness-Seconds").Single(), CultureInfo.InvariantCulture) >= 2);
        await restart;

        await rig.CaughtUpAsync();
        var after = await rig.GetAsync($"{Api}/models", viewer);
        Assert.Equal("0", after.Response.Headers.GetValues("X-Data-Staleness-Seconds").Single());
    }

    [Fact]
    public async Task TheTraceReturnsTheStoredChainInOneStatement_AndOnlyItsWindowsReadings()
    {
        var seeded = await SeedAsync();
        var viewer = rig.Mint("trace001", "viewer");

        // Twenty equipment and 24 h of per-second readings beside the chain, written straight to the
        // table: what is tested is the query against a realistic table, not the projector's speed.
        await using (var bulk = rig.Db.CreateCommand("""
            SELECT operations.ensure_partition('telemetry_reading_1s', '2026-09-20T00:00:00Z');
            SELECT operations.ensure_partition('telemetry_reading_1s', '2026-09-21T00:00:00Z');
            INSERT INTO operations.telemetry_reading_1s
            SELECT 'eq-9' || lpad(e::text, 5, '0'), t, t, n, t, t, 40, 2, 12, 400, 1780, 42, 100, 'GOOD', '[]'::jsonb, 'RUNNING',
                   gen_random_uuid(), 'seed', 0, n
            FROM generate_series(1, 20) e,
                 LATERAL (SELECT g AS n, '2026-09-20T00:00:00Z'::timestamptz + make_interval(secs => g) AS t
                          FROM generate_series(0, 86399) g) s
            ON CONFLICT DO NOTHING;
            ANALYZE operations.telemetry_reading_1s;
            """))
        {
            bulk.CommandTimeout = 300;
            await bulk.ExecuteNonQueryAsync();
        }

        // Count the SQL statements the trace request issues, from Npgsql's own diagnostics.
        var statements = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                // The projector is caught up and idle, so every statement in this window is the API's.
                if (a.Source.Name == "Npgsql")
                {
                    Interlocked.Increment(ref statements);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        var traced = await rig.GetAsync($"{Api}/trace/{seeded.Correlation}", viewer);
        Assert.Equal(1, statements);

        using (var chain = JsonDocument.Parse(traced.Body))
        {
            var root = chain.RootElement;
            Assert.Equal(seeded.Correlation, root.GetProperty("prediction"u8).GetProperty("correlationId"u8).GetString());
            Assert.Single(root.GetProperty("decisions"u8).EnumerateArray());
            Assert.Single(root.GetProperty("commands"u8).EnumerateArray());

            // [startUtc, endUtc): the two readings inside, not the one a second before.
            var readings = root.GetProperty("window"u8).GetProperty("readings"u8).EnumerateArray().ToList();
            Assert.Equal(2, readings.Count);
            Assert.All(readings, r => Assert.True(
                DateTime.Parse(r.GetProperty("secondUtc"u8).GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal) >= seeded.WindowStart));
        }

        // P95 within 200 ms over 100 requests (TARGET in §18; asserted here as TRACE-001 requires).
        var timings = new List<double>();
        for (var i = 0; i < 100; i++)
        {
            var clock = Stopwatch.StartNew();
            await rig.GetAsync($"{Api}/trace/{seeded.Correlation}", viewer);
            timings.Add(clock.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        Assert.True(timings[94] <= 200, $"P95 {timings[94]:0.0} ms");
    }

    [Fact]
    public async Task AuditRecordsPageByKeysetAcrossAPartitionBoundary_EachExistingOneExactlyOnce()
    {
        var eq = ProjectionRig.NewEquipment();
        var viewer = rig.Mint("audit001", "viewer");
        var existing = new List<string>();

        async Task<string> DecisionAsync(DateTime at)
        {
            var d = ProjectionRig.Example("safety-decision.clamped");
            var id = ProjectionRig.NewId();
            d["decisionId"] = id;
            d["equipmentId"] = eq;
            d["decidedAtUtc"] = At(at);
            d["occurredAtUtc"] = At(at);
            d["correlationId"] = ProjectionRig.NewId();
            await ProjectionRig.ProduceAsync(rig.Producer, "factory.safety-decisions.v1", eq, d);
            return id;
        }

        // 25 decisions either side of a month boundary - two monthly partitions.
        var start = new DateTime(2026, 8, 31, 23, 50, 0, DateTimeKind.Utc);
        for (var i = 0; i < 25; i++)
        {
            existing.Add(await DecisionAsync(start.AddMinutes(i)));
        }

        await rig.CaughtUpAsync();

        var seen = new List<string>();
        string? cursor = null;
        var page = 0;
        do
        {
            var path = $"{Api}/equipment/{eq}/decisions?limit=10" + (cursor is null ? "" : $"&cursor={cursor}");
            using var doc = JsonDocument.Parse((await rig.GetAsync(path, viewer)).Body);
            seen.AddRange(doc.RootElement.GetProperty("items"u8).EnumerateArray().Select(d => d.GetProperty("decisionId"u8).GetString()!));
            cursor = doc.RootElement.GetProperty("nextCursor"u8).GetString();

            // Rows arriving between pages: one newer than everything, one older than the cursor.
            if (page++ == 0)
            {
                await DecisionAsync(start.AddHours(1));
                await DecisionAsync(start.AddHours(-1));
                await rig.CaughtUpAsync();
            }
        }
        while (cursor is not null);

        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Subset(seen.ToHashSet(), existing.ToHashSet());

        // Range: from inclusive, to exclusive.
        var range = $"{Api}/equipment/{eq}/decisions?from={At(start.AddMinutes(5))}&to={At(start.AddMinutes(15))}&limit=500";
        using (var doc = JsonDocument.Parse((await rig.GetAsync(range, viewer)).Body))
        {
            Assert.Equal(existing.Skip(5).Take(10).Reverse(), doc.RootElement.GetProperty("items"u8).EnumerateArray().Select(d => d.GetProperty("decisionId"u8).GetString()));
        }
    }
}
