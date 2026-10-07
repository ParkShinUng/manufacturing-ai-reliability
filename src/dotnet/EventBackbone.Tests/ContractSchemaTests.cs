using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mair.EventBackbone.Tests;

/// <summary>
/// The validator itself, before any Kafka (<c>ADR-0022</c> conditions 1, 2 and 5).
/// <para>
/// <c>AC-027</c> proves DLQ routing, not validator correctness: a validator that caught only the one
/// invalid record <c>KAFKA-002</c> produces would pass it while accepting other invalid records.
/// So every keyword the contract schemas use has a case here, and the inventory test fails if a
/// schema starts using a keyword that has none.
/// </para>
/// </summary>
public sealed class ContractSchemaTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "contracts", "jsonschema", "v1")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("could not find the repository root from the test directory");
    }

    private static string SchemaDir => Path.Combine(Root, "contracts", "jsonschema", "v1");

    private static ReadOnlyMemory<byte> Bytes(string json) => Encoding.UTF8.GetBytes(json);

    // ------------------------------------------------------------ condition 2: every schema, a valid document

    /// <summary>
    /// The pairing comes from <c>contracts/examples/_manifest.json</c>, the same file
    /// <c>validate_examples.mjs</c> reads. Guessing it from file names would make this test and that
    /// one disagree about what "every example" means.
    /// </summary>
    public static TheoryData<string, string> Examples()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(FindRoot(), "contracts", "examples", "_manifest.json")))!.AsObject();
        var data = new TheoryData<string, string>();
        foreach (var (example, schema) in manifest)
        {
            data.Add(example, schema!.GetValue<string>());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void EveryDocumentedExampleIsValidAgainstItsSchema(string example, string schemaName)
    {
        var schema = ContractSchema.Load(Path.Combine(SchemaDir, schemaName));

        var verdict = schema.Validate(Bytes(File.ReadAllText(Path.Combine(Root, "contracts", "examples", example))));

        Assert.True(verdict.Valid, verdict.Reason);
    }

    [Fact]
    public void EveryExampleFileIsInTheManifest()
    {
        // An example that is not in the manifest would be validated by nothing.
        var listed = Examples().Select(row => (string)row[0]).ToHashSet();
        var files = Directory.GetFiles(Path.Combine(Root, "contracts", "examples"), "*.json")
            .Select(f => Path.GetFileName(f)!)
            .Where(f => f != "_manifest.json");

        Assert.All(files, f => Assert.Contains(f, listed));
    }

    [Fact]
    public void EverySchemaLoads()
    {
        var schemas = Directory.GetFiles(SchemaDir, "*.schema.json");

        // 9 since 2026-10-07: dataset-manifest.schema.json (OD-025).
        Assert.Equal(9, schemas.Length);
        Assert.All(schemas, s => ContractSchema.Load(s));
    }

    // ------------------------------------------------------------ condition 1: every keyword, an invalid document

    /// <summary>
    /// One case per validation keyword the contract uses: a minimal schema exercising it, a
    /// document it must reject, and one it must accept — so a validator that rejected everything
    /// fails too.
    /// </summary>
    public static TheoryData<string, string, string, string> Keywords() => new()
    {
        { "type", """{"type":"integer"}""", "\"x\"", "1" },
        { "const", """{"const":1}""", "2", "1" },
        { "enum", """{"enum":["A","B"]}""", "\"C\"", "\"A\"" },
        { "required", """{"type":"object","required":["a"]}""", "{}", """{"a":1}""" },
        { "properties", """{"type":"object","properties":{"a":{"type":"integer"}}}""", """{"a":"x"}""", """{"a":1}""" },
        { "additionalProperties", """{"type":"object","properties":{"a":{}},"additionalProperties":false}""", """{"a":1,"b":2}""", """{"a":1}""" },
        { "minimum", """{"minimum":0}""", "-1", "0" },
        { "maximum", """{"maximum":10}""", "11", "10" },
        { "exclusiveMinimum", """{"exclusiveMinimum":0}""", "0", "0.1" },
        { "minLength", """{"type":"string","minLength":2}""", "\"a\"", "\"ab\"" },
        { "maxLength", """{"type":"string","maxLength":2}""", "\"abc\"", "\"ab\"" },
        { "pattern", """{"type":"string","pattern":"^eq-[0-9]{3}$"}""", "\"eq-1\"", "\"eq-001\"" },
        { "format", """{"type":"string","format":"uuid"}""", "\"not-a-uuid\"", "\"7b52009b-64fd-4a1f-9b1e-0a2f4c6d8e10\"" },
        { "items", """{"type":"array","items":{"type":"integer"}}""", "[1,\"x\"]", "[1,2]" },
        { "prefixItems", """{"type":"array","prefixItems":[{"type":"string"},{"type":"integer"}]}""", "[1,1]", "[\"a\",1]" },
        { "minItems", """{"type":"array","minItems":1}""", "[]", "[1]" },
        { "maxItems", """{"type":"array","maxItems":1}""", "[1,2]", "[1]" },
        { "uniqueItems", """{"type":"array","uniqueItems":true}""", "[1,1]", "[1,2]" },
        { "minProperties", """{"type":"object","minProperties":1}""", "{}", """{"a":1}""" },
        { "allOf", """{"allOf":[{"minimum":0},{"maximum":10}]}""", "11", "5" },
        { "not", """{"not":{"const":0}}""", "0", "1" },
        { "if", """{"if":{"properties":{"k":{"const":"a"}}},"then":{"required":["x"]}}""", """{"k":"a"}""", """{"k":"a","x":1}""" },
        { "then", """{"if":{"properties":{"k":{"const":"a"}}},"then":{"properties":{"x":{"type":"integer"}}}}""", """{"k":"a","x":"no"}""", """{"k":"b","x":"no"}""" },
    };

    private const string Draft = "\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",";

    [Theory]
    [MemberData(nameof(Keywords))]
    public void EachKeywordRejectsWhatItShouldAndAcceptsWhatItShould(string keyword, string schema, string invalid, string valid)
    {
        var contract = ContractSchema.FromText("{" + Draft + schema[1..], keyword);

        var rejected = contract.Validate(Bytes(invalid));
        var accepted = contract.Validate(Bytes(valid));

        Assert.False(rejected.Valid, $"{keyword}: {invalid} was accepted");
        Assert.Equal(RejectionClass.SchemaInvalid, rejected.Rejection);
        Assert.True(accepted.Valid, $"{keyword}: {valid} was rejected — {accepted.Reason}");
    }

    [Fact]
    public void EveryKeywordTheContractUsesHasACase()
    {
        // If a schema starts using a keyword with no case above, this fails - so a validator gap
        // cannot hide behind a keyword nobody thought to test (ADR-0022).
        string[] validationKeywords =
        [
            "type", "const", "enum", "required", "properties", "additionalProperties", "items", "prefixItems",
            "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "minLength", "maxLength", "pattern",
            "format", "minItems", "maxItems", "uniqueItems", "minProperties", "maxProperties", "allOf", "anyOf",
            "oneOf", "not", "if", "then", "else", "dependentRequired", "dependentSchemas", "patternProperties",
            "propertyNames", "contains", "multipleOf",
        ];

        var used = new HashSet<string>();
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (k, v) in o)
                    {
                        if (validationKeywords.Contains(k))
                        {
                            used.Add(k);
                        }

                        Walk(v);
                    }

                    break;
                case JsonArray a:
                    foreach (var v in a)
                    {
                        Walk(v);
                    }

                    break;
            }
        }

        foreach (var file in Directory.GetFiles(SchemaDir, "*.schema.json"))
        {
            Walk(JsonNode.Parse(File.ReadAllText(file)));
        }

        var covered = Keywords().Select(row => (string)row[0]).ToHashSet();
        var uncovered = used.Except(covered).Order().ToList();

        Assert.True(uncovered.Count == 0, $"keywords used by the contract with no validator case: {string.Join(", ", uncovered)}");
    }

    // ------------------------------------------------------------ against the real contract

    [Fact]
    public void TheRealTelemetrySchemaRejectsAWrongSchemaVersion_TheCaseNJsonSchemaMissed()
    {
        var schema = ContractSchema.Load(Path.Combine(SchemaDir, "telemetry.schema.json"));
        var good = File.ReadAllText(Path.Combine(Root, "contracts", "examples", "telemetry.healthy.json"));

        var verdict = schema.Validate(Bytes(good.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2")));

        Assert.False(verdict.Valid);
        Assert.Contains("telemetry.schema.json", verdict.Reason);
    }

    // ------------------------------------------------------------ condition 5

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("\u0001\u0002")]
    public void InputThatIsNotJsonIsUnparseable_NotAnException(string raw)
    {
        var schema = ContractSchema.Load(Path.Combine(SchemaDir, "telemetry.schema.json"));

        var verdict = schema.Validate(Bytes(raw));

        Assert.False(verdict.Valid);
        Assert.Equal(RejectionClass.Unparseable, verdict.Rejection);
    }

    [Fact]
    public void ValidationDoesNotAlterTheBytes()
    {
        // The DLQ carries what was judged. If validation touched the buffer, it would carry
        // something else.
        var schema = ContractSchema.Load(Path.Combine(SchemaDir, "telemetry.schema.json"));
        var original = Encoding.UTF8.GetBytes("""{"schemaVersion": 2}""");
        var copy = original.ToArray();

        schema.Validate(original);

        Assert.Equal(copy, original);
        _ = JsonDocument.Parse(original); // still the same, still parseable
    }
}
