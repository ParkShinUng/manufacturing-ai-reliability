using Corvus.Text.Json;
using Corvus.Text.Json.Validator;

namespace Mair.EventBackbone;

/// <summary>Why a record was not accepted. Carried in <c>x-dlq-error-class</c>.</summary>
public enum RejectionClass
{
    /// <summary>Parsed as JSON, failed the contract schema. DLQ'd on the first attempt (§9).</summary>
    SchemaInvalid,

    /// <summary>Not JSON at all. Also first attempt: no retry can make bytes parse.</summary>
    Unparseable,

    /// <summary>Valid, but the handler failed on every attempt §9 allows.</summary>
    HandlerFailed,
}

/// <summary>The outcome of validating one record's bytes.</summary>
public sealed record SchemaVerdict(bool Valid, RejectionClass? Rejection, string Reason)
{
    public static readonly SchemaVerdict Accepted = new(true, null, "");
}

/// <summary>
/// One authoritative contract schema, loaded from <c>contracts/jsonschema/v1/</c> as it is
/// (<c>ADR-0017</c>, <c>ADR-0022</c>).
/// <para>
/// Validates the record's <b>raw UTF-8 bytes</b>. Nothing is deserialised and re-serialised on the
/// way, so the bytes that were judged are exactly the bytes that reach the DLQ.
/// </para>
/// </summary>
public sealed class ContractSchema
{
    /// <summary>A DLQ header should explain, not reproduce the document. Kept short on purpose.</summary>
    private const int MaxReasons = 5;

    private readonly JsonSchema _schema;

    private ContractSchema(JsonSchema schema, string name)
    {
        _schema = schema;
        Name = name;
    }

    public string Name { get; }

    public static ContractSchema Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new ContractSchema(JsonSchema.FromFile(path), Path.GetFileName(path));
    }

    /// <summary>
    /// For schemas that are not files, such as a test's per-keyword cases. Each needs its own
    /// canonical URI: the validator caches by URI, and two schemas sharing one would share a result.
    /// </summary>
    public static ContractSchema FromText(string schemaJson, string name) =>
        new(JsonSchema.FromText(schemaJson, $"urn:mair:schema:{Uri.EscapeDataString(name)}", refreshCache: true), name);

    public SchemaVerdict Validate(ReadOnlyMemory<byte> utf8Json)
    {
        using var collector = JsonSchemaResultsCollector.Create(JsonSchemaResultsLevel.Basic);

        bool valid;
        try
        {
            valid = _schema.Validate(utf8Json, collector);
        }
        catch (JsonException ex)
        {
            // Input that is not JSON throws rather than returning false (ADR-0022, condition 5).
            // Letting it escape would crash the consumer on one corrupt record, which is exactly
            // what the DLQ is for. The concrete type Corvus throws is internal, so this catches its
            // public base - Corvus.Text.Json.JsonException, not System.Text.Json's.
            return new SchemaVerdict(false, RejectionClass.Unparseable, $"not valid JSON: {ex.Message}");
        }

        if (valid)
        {
            return SchemaVerdict.Accepted;
        }

        var reasons = new List<string>();
        foreach (var result in collector.EnumerateResults())
        {
            if (result.IsMatch)
            {
                continue;
            }

            var message = result.GetMessageText();
            var at = result.GetDocumentEvaluationLocationText();
            reasons.Add(string.IsNullOrEmpty(at) ? message : $"{at}: {message}");

            if (reasons.Count == MaxReasons)
            {
                break;
            }
        }

        return new SchemaVerdict(
            false,
            RejectionClass.SchemaInvalid,
            reasons.Count == 0 ? $"failed {Name}" : $"failed {Name}: {string.Join("; ", reasons)}");
    }
}
