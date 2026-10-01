using System.Text;

namespace Mair.OperationsService.Tests;

/// <summary>
/// ADR-0024 B3: the AC-030 harness, against the real contract, catches each kind of mismatch it
/// claims to — one deliberately wrong response per check, and the right one passing beside it.
/// </summary>
public sealed class OpenApiHarnessTests
{
    private static readonly string Contract = FindContract();

    private static string FindContract()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "contracts", "openapi", "operations-api-v1.yaml");
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new InvalidOperationException("could not find the OpenAPI contract");
    }

    private const string Summary = """
        {"items":[{"equipmentId":"eq-001","state":"RUNNING","controlMode":null,"aiEligible":true,
                   "qualityOverall":"GOOD","activeReasonCodes":[],"updatedAtUtc":"2026-10-01T00:00:00.000Z"}],
         "nextCursor":null}
        """;

    private const string Problem = """{"type":"about:blank","title":"Unauthorized","status":401}""";

    private static ObservedResponse Response(string path, int status, string? contentType, string body, Dictionary<string, string>? headers = null) =>
        new("GET", path, status, contentType, headers ?? new() { ["X-Data-Staleness-Seconds"] = "0" }, Encoding.UTF8.GetBytes(body));

    private static OpenApiContract Load() => OpenApiContract.Load(Contract);

    [Fact]
    public void AConformingResponsePasses_IncludingANullTheContractAllows()
    {
        Assert.Empty(Load().Check(Response("/equipment", 200, "application/json; charset=utf-8", Summary)));
    }

    [Fact]
    public void AnErrorResponseIsCheckedToo()
    {
        Assert.Empty(Load().Check(Response("/equipment", 401, "application/problem+json", Problem, [])));
    }

    [Fact]
    public void AnUndocumentedStatusFails()
    {
        Assert.Contains(Load().Check(Response("/equipment", 418, "application/json", "{}")), f => f.Contains("status not documented"));
    }

    [Fact]
    public void AWrongContentTypeFails()
    {
        Assert.Contains(Load().Check(Response("/equipment", 401, "application/json", Problem, [])), f => f.Contains("content type"));
    }

    [Fact]
    public void AMissingStalenessHeaderFails()
    {
        Assert.Contains(Load().Check(Response("/equipment", 200, "application/json", Summary, [])), f => f.Contains("X-Data-Staleness-Seconds"));
    }

    [Fact]
    public void AHeaderValueOutsideItsSchemaFails()
    {
        Assert.Contains(Load().Check(Response("/equipment", 200, "application/json", Summary, new() { ["X-Data-Staleness-Seconds"] = "soon" })),
            f => f.Contains("header X-Data-Staleness-Seconds"));
    }

    [Fact]
    public void ABodyMissingARequiredKeyFails()
    {
        // nextCursor is required even when null: the key stays, so the shape never changes (OD-012).
        Assert.Contains(Load().Check(Response("/equipment", 200, "application/json", """{"items":[]}""")), f => f.Contains("body"));
    }

    [Fact]
    public void AValueOutsideAnAnyOfFails()
    {
        // controlMode is ControlMode or null - not a value the enum does not have.
        var body = Summary.Replace("\"controlMode\":null", "\"controlMode\":\"MANUAL_OPERATOR\"", StringComparison.Ordinal);
        Assert.Contains(Load().Check(Response("/equipment", 200, "application/json", body)), f => f.Contains("body"));
    }

    [Fact]
    public void AMalformedUuidFails_FormatIsAsserted()
    {
        var trace = """
            {"correlationId":"not-a-uuid","window":null,"prediction":null,"decision":null,"command":null}
            """;
        Assert.Contains(Load().Check(Response("/trace/7b52009b-64fd-4a1f-9b1e-0a2f4c6d8e10", 200, "application/json", trace)), f => f.Contains("body"));

        var valid = trace.Replace("not-a-uuid", "7b52009b-64fd-4a1f-9b1e-0a2f4c6d8e10", StringComparison.Ordinal);
        Assert.Empty(Load().Check(Response("/trace/7b52009b-64fd-4a1f-9b1e-0a2f4c6d8e10", 200, "application/json", valid)));
    }

    [Fact]
    public void AMalformedDateTimeFails_FormatIsAsserted()
    {
        var body = Summary.Replace("2026-10-01T00:00:00.000Z", "yesterday", StringComparison.Ordinal);
        Assert.Contains(Load().Check(Response("/equipment", 200, "application/json", body)), f => f.Contains("body"));
    }

    [Fact]
    public void ATemplateMatchesItsParameterisedPath()
    {
        var problem = """{"type":"about:blank","title":"Not Found","status":404}""";
        Assert.Empty(Load().Check(Response("/equipment/eq-001", 404, "application/problem+json", problem, [])));
    }

    [Fact]
    public void CoverageReportsEveryDocumentedResponseNotYetSeen_AndShrinksAsTheyAre()
    {
        var contract = Load();
        var all = contract.Uncovered();
        Assert.Contains(("/equipment", "GET", 401), all);

        contract.Check(Response("/equipment", 401, "application/problem+json", Problem, []));

        Assert.DoesNotContain(("/equipment", "GET", 401), contract.Uncovered());
        Assert.Equal(all.Count - 1, contract.Uncovered().Count);
    }
}
