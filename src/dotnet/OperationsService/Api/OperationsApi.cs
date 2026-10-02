using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Mair.OperationsService.Auth;
using Microsoft.AspNetCore.Http.Timeouts;
using Npgsql;

namespace Mair.OperationsService.Api;

/// <summary>
/// The Operations API (<c>contracts/openapi/operations-api-v1.yaml</c>, <c>OPERATIONS_API.md</c>):
/// read-only, never in the command path. Every response is one of the contract's documented
/// responses, and every error is problem+json (AC-030).
/// </summary>
public static partial class OperationsApi
{
    private const string Base = "/api/v1";

    /// <summary>The topics whose projections back each route: the staleness header is their maximum (OD-018).</summary>
    private static readonly string[] EquipmentTopics =
        ["factory.equipment-states.v1", "factory.telemetry.v1", "factory.predictions.v1", "factory.safety-decisions.v1", "factory.control-outcomes.v1"];

    private static readonly string[] AllTopics = [.. EquipmentTopics, "factory.model-deployments.v1"];

    private static readonly string[] EquipmentStates = ["OFFLINE", "CONNECTING", "IDLE", "RUNNING", "DEGRADED", "FAULT", "STOPPING"];
    private static readonly string[] ControlModes = ["NORMAL_RULE", "AI_ASSISTED", "SAFE_FALLBACK", "STOP_REQUIRED"];
    private static readonly string[] DecisionKinds = ["ACCEPT", "REJECT", "FALLBACK"];

    /// <summary>Services the API needs: rate limiting and the 5 s request timeout (§12, §13).</summary>
    public static IServiceCollection AddOperationsApi(this IServiceCollection services)
    {
        services.AddRateLimiter(o =>
        {
            // OD-018: 20 requests/s per token subject, burst 40. The subject is the identity; an
            // unauthenticated caller shares one bucket and is refused by authorization anyway.
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetTokenBucketLimiter(context.User.FindFirst("sub")?.Value ?? "(anonymous)", _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 40,
                    TokensPerPeriod = 20,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
            o.OnRejected = (context, _) => new ValueTask(Problems.WriteAsync(context.HttpContext, StatusCodes.Status429TooManyRequests,
                "Too Many Requests", "rate limit: 20 requests/s per subject, burst 40"));
        });

        services.AddRequestTimeouts(o => o.DefaultPolicy = new RequestTimeoutPolicy
        {
            Timeout = TimeSpan.FromSeconds(5),
            // The contract has no 504: a request that outlives its budget is the store not answering.
            WriteTimeoutResponse = context => Problems.WriteAsync(context, StatusCodes.Status503ServiceUnavailable,
                "Service Unavailable", "the request did not complete within 5 s"),
        });

        return services;
    }

    public static WebApplication MapOperationsApi(this WebApplication app, NpgsqlDataSource db, IProjectionStatus projection, IKafkaProbe kafka, TimeProvider time)
    {
        var read = new ReadModel(db);
        var breaker = new CircuitBreaker(time);

        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.UseRequestTimeouts();

        // The readiness gate (OD-018): no read model is served as current before every projector has
        // been caught up once since start.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(Base) && !context.Request.Path.StartsWithSegments(Base + "/platform/health")
                && AllTopics.Any(t => projection.LastCaughtUp(t) is null))
            {
                await Problems.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "Service Unavailable",
                    "the read models are still being built: not every projector has caught up yet");
                return;
            }

            await next(context);
        });

        var api = app.MapGroup(Base).RequireAuthorization(OperationsAuth.ReadPolicy);

        api.MapGet("/equipment", (HttpContext http, string? limit, string? cursor, string? state, string? controlMode) =>
            Serve(http, db, breaker, projection, time, EquipmentTopics, async ct =>
            {
                var after = cursor is null ? null : Cursor.Decode(cursor, "e")["e"];
                return await read.EquipmentListAsync(Limit(limit), after, OneOf(state, EquipmentStates, "state"),
                    OneOf(controlMode, ControlModes, "controlMode"), ct);
            }));

        api.MapGet("/equipment/{equipmentId}", (HttpContext http, string equipmentId) =>
            Serve(http, db, breaker, projection, time, EquipmentTopics, ct => read.EquipmentDetailAsync(EquipmentId(equipmentId), ct)));

        api.MapGet("/equipment/{equipmentId}/decisions", (HttpContext http, string equipmentId, string? from, string? to, string? limit, string? cursor, string? decision) =>
            Serve(http, db, breaker, projection, time, ["factory.safety-decisions.v1"], ct =>
                read.DecisionsAsync(EquipmentId(equipmentId), Time(from, "from"), Time(to, "to"), OneOf(decision, DecisionKinds, "decision"),
                    Limit(limit), KeysetAfter(cursor), ct)));

        api.MapGet("/equipment/{equipmentId}/commands", (HttpContext http, string equipmentId, string? from, string? to, string? limit, string? cursor) =>
            Serve(http, db, breaker, projection, time, ["factory.control-outcomes.v1"], ct =>
                read.CommandsAsync(EquipmentId(equipmentId), Time(from, "from"), Time(to, "to"), Limit(limit), KeysetAfter(cursor), ct)));

        api.MapGet("/trace/{correlationId}", (HttpContext http, string correlationId) =>
            Serve(http, db, breaker, projection, time, ["factory.predictions.v1", "factory.safety-decisions.v1", "factory.control-outcomes.v1", "factory.telemetry.v1"],
                ct => read.TraceAsync(Guid.TryParseExact(correlationId, "D", out var id) ? id : throw new BadRequestException("correlationId is not a uuid"), ct)));

        // Cast: a lambda taking only HttpContext would bind as a RequestDelegate and drop the result.
        api.MapGet("/models", (Delegate)((HttpContext http) =>
            Serve(http, db, breaker, projection, time, ["factory.model-deployments.v1"], async ct => (byte[]?)await read.ModelsAsync(time.GetUtcNow(), ct))));

        api.MapGet("/platform/health", async (HttpContext http) =>
        {
            // Answers without PostgreSQL (§11): the database-derived field is omitted, not invented.
            double? fallback = null;
            try
            {
                using var quick = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
                quick.CancelAfter(TimeSpan.FromSeconds(1));
                fallback = await read.FallbackRatePctAsync(quick.Token);
            }
            catch (Exception ex) when (Unavailable(ex) || ex is OperationCanceledException)
            {
                fallback = null;
            }

            http.Response.ContentType = "application/json";
            await using var w = new System.Text.Json.Utf8JsonWriter(http.Response.Body);
            w.WriteStartObject();
            w.WriteNull("inferenceAvailable"u8);
            w.WriteNull("supervisorHealthy"u8);
            w.WriteNull("controlServiceHealthy"u8);
            w.WriteBoolean("kafkaReachable"u8, kafka.Reachable());
            w.WriteNumber("maxConsumerLag"u8, projection.MaxLag);
            if (projection.PredictionRecordAge is { } age)
            {
                w.WriteNumber("predictionRecordAgeSeconds"u8, Math.Round(age.TotalSeconds, 3));
            }

            if (fallback is { } pct)
            {
                w.WriteNumber("fallbackRatePct"u8, pct);
            }

            w.WriteEndObject();
        });

        return app;
    }

    /// <summary>
    /// Runs a read, maps its outcome to a documented response: the body, <c>404</c> for <c>null</c>,
    /// <c>400</c> for a bad parameter, <c>503</c> when PostgreSQL does not answer — and the staleness
    /// header on every success.
    /// </summary>
    private static async Task<IResult> Serve(HttpContext http, NpgsqlDataSource db, CircuitBreaker breaker, IProjectionStatus projection, TimeProvider time, string[] topics, Func<CancellationToken, Task<byte[]?>> read)
    {
        if (!breaker.TryEnter())
        {
            return Problems.Result(StatusCodes.Status503ServiceUnavailable, "Service Unavailable",
                "the operational store failed 5 times in a row; it is not asked again for 30 s (circuit open, OD-022)");
        }

        byte[]? body;
        try
        {
            body = await read(http.RequestAborted);
            breaker.Success();
        }
        catch (BadRequestException ex)
        {
            // Refused before the store was asked: says nothing about the store.
            breaker.Neutral();
            return Problems.Result(StatusCodes.Status400BadRequest, "Bad Request", ex.Message);
        }
        catch (Exception ex) when (Unavailable(ex) || (ex is OperationCanceledException && !http.RequestAborted.IsCancellationRequested))
        {
            breaker.Failure();

            // Broken connections would otherwise each fail once more after the store is back.
            db.Clear();
            return Problems.Result(StatusCodes.Status503ServiceUnavailable, "Service Unavailable", "the operational store did not answer; control is unaffected (F12)");
        }
        catch
        {
            breaker.Neutral();
            throw;
        }

        if (body is null)
        {
            return Problems.Result(StatusCodes.Status404NotFound, "Not Found");
        }

        // OD-018: 0 while every projector behind this route is caught up; otherwise seconds since the
        // least recently caught up of them was.
        var now = time.GetUtcNow();
        var stalest = topics.Max(t => projection.CaughtUp(t) ? TimeSpan.Zero : now - (projection.LastCaughtUp(t) ?? now));
        http.Response.Headers["X-Data-Staleness-Seconds"] = Math.Floor(Math.Max(0, stalest.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return Results.Bytes(body, "application/json");
    }

    /// <summary>The store not answering, as OD-022 lists it - a SQL error is a defect, not an outage.</summary>
    internal static bool Unavailable(Exception ex) => Database.StoreFailures.IsUnavailable(ex);

    private static int Limit(string? limit)
    {
        if (limit is null)
        {
            return 100;
        }

        return int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 500
            ? n
            : throw new BadRequestException("limit must be an integer from 1 to 500");
    }

    private static string EquipmentId(string id) =>
        EquipmentIdPattern().IsMatch(id) ? id : throw new BadRequestException("equipmentId must match ^eq-[0-9]{3,6}$");

    private static string? OneOf(string? value, string[] allowed, string name) =>
        value is null || allowed.Contains(value) ? value : throw new BadRequestException($"{name} must be one of {string.Join(", ", allowed)}");

    private static DateTime? Time(string? value, string name) =>
        value is null ? null
        : DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) && DateTimeText().IsMatch(value)
            ? t
            : throw new BadRequestException($"{name} is not an RFC 3339 date-time");

    private static (DateTime At, Guid Id)? KeysetAfter(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        var key = Cursor.Decode(cursor, "t", "i");
        return (Time(key["t"], "cursor")!.Value, Guid.TryParseExact(key["i"], "D", out var id) ? id : throw new BadRequestException("cursor has the wrong shape"));
    }

    [GeneratedRegex("^eq-[0-9]{3,6}$")]
    private static partial Regex EquipmentIdPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex DateTimeText();
}
