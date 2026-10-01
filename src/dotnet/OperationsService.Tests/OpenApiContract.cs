using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mair.EventBackbone;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Mair.OperationsService.Tests;

/// <summary>One response as the API gave it.</summary>
public sealed record ObservedResponse(
    string Method, string Path, int Status, string? ContentType, IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>
/// The AC-030 harness pinned by <c>ADR-0024</c>: the OpenAPI contract, read as it is from
/// <c>contracts/openapi/</c>, and five checks on every response — documented status, media type,
/// documented headers, body schema, <c>format</c> — plus coverage of every documented status.
/// <para>
/// OpenAPI 3.1 schemas are JSON Schema 2020-12, so the body is validated by the repository's one
/// validator (<c>ADR-0022</c>) through a wrapper: the whole contract document with a <c>$ref</c> to
/// the selected schema's location, so the schema's own <c>$ref</c>s resolve where they were written.
/// </para>
/// </summary>
public sealed partial class OpenApiContract
{
    private readonly JsonObject _doc;
    private readonly string _docJson;
    private readonly Dictionary<string, ContractSchema> _schemas = [];
    private readonly HashSet<(string Template, string Method, int Status)> _seen = [];

    private OpenApiContract(JsonObject doc)
    {
        _doc = doc;
        _docJson = doc.ToJsonString();
    }

    public static OpenApiContract Load(string path)
    {
        var yaml = new YamlStream();
        using (var reader = new StreamReader(path, Encoding.UTF8))
        {
            yaml.Load(reader);
        }

        return new OpenApiContract(ToJson(yaml.Documents[0].RootNode)!.AsObject());
    }

    /// <summary>Every <c>(path template, method, status)</c> the contract documents.</summary>
    public IReadOnlySet<(string Template, string Method, int Status)> Documented()
    {
        var all = new HashSet<(string, string, int)>();
        foreach (var (template, item) in _doc["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                foreach (var (status, _) in operation!["responses"]!.AsObject())
                {
                    all.Add((template, method.ToUpperInvariant(), int.Parse(status, CultureInfo.InvariantCulture)));
                }
            }
        }

        return all;
    }

    /// <summary>Documented responses no check has seen yet, minus recorded exclusions.</summary>
    public IReadOnlyList<(string Template, string Method, int Status)> Uncovered(Func<(string Template, string Method, int Status), bool>? excluded = null) =>
        Documented().Where(d => !_seen.Contains(d) && excluded?.Invoke(d) != true).OrderBy(d => d.Template).ThenBy(d => d.Status).ToList();

    /// <returns>Every mismatch; empty when the response conforms.</returns>
    public IReadOnlyList<string> Check(ObservedResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var failures = new List<string>();
        var method = response.Method.ToLowerInvariant();

        var template = MatchTemplate(response.Path);
        if (template is null || _doc["paths"]![template]![method] is not JsonObject operation)
        {
            return [$"{response.Method} {response.Path}: no such operation in the contract"];
        }

        var at = $"{response.Method} {template} {response.Status}";
        var statusKey = response.Status.ToString(CultureInfo.InvariantCulture);
        if (operation["responses"]![statusKey] is not JsonObject declared)
        {
            return [$"{at}: status not documented"];
        }

        _seen.Add((template, response.Method.ToUpperInvariant(), response.Status));
        var (responseNode, responsePointer) = Resolve(declared, $"/paths/{Escape(template)}/{method}/responses/{statusKey}");

        // 3 — headers.
        if (responseNode["headers"] is JsonObject headers)
        {
            foreach (var (name, headerDecl) in headers)
            {
                var (header, headerPointer) = Resolve(headerDecl!.AsObject(), $"{responsePointer}/headers/{Escape(name)}");
                if (!response.Headers.TryGetValue(name, out var value))
                {
                    failures.Add($"{at}: header {name} is documented and absent");
                    continue;
                }

                var reasons = Validate($"{headerPointer}/schema", HeaderAsJson(header["schema"]!, value));
                failures.AddRange(reasons.Select(r => $"{at}: header {name}={value}: {r}"));
            }
        }

        // 2 and 4 — media type, then the body against the schema selected by it.
        if (responseNode["content"] is JsonObject content)
        {
            var mediaType = response.ContentType?.Split(';')[0].Trim();
            if (mediaType is null || content[mediaType] is not JsonObject media)
            {
                failures.Add($"{at}: content type '{response.ContentType}' is not one of [{string.Join(", ", content.Select(c => c.Key))}]");
            }
            else
            {
                failures.AddRange(Validate($"{responsePointer}/content/{Escape(mediaType)}/schema", response.Body)
                    .Select(r => $"{at}: body: {r}"));
            }
        }
        else if (response.Body.Length > 0)
        {
            failures.Add($"{at}: a body was returned where the contract documents none");
        }

        return failures;
    }

    /// <summary>The literal segments must match; a <c>{param}</c> segment matches any one segment.</summary>
    private string? MatchTemplate(string path)
    {
        var parts = path.Split('?')[0].Trim('/').Split('/');
        return _doc["paths"]!.AsObject()
            .Select(p => p.Key)
            .Where(t =>
            {
                var segments = t.Trim('/').Split('/');
                return segments.Length == parts.Length
                    && segments.Zip(parts).All(s => (s.First.StartsWith('{') && s.First.EndsWith('}')) || s.First == s.Second);
            })
            // A literal match outranks a parameter match.
            .OrderBy(t => t.Count(c => c == '{'))
            .FirstOrDefault();
    }

    private (JsonObject Node, string Pointer) Resolve(JsonObject node, string pointer)
    {
        while (node["$ref"] is JsonValue reference)
        {
            var target = ((string)reference!).TrimStart('#');
            pointer = target;
            node = target.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Aggregate((JsonNode)_doc, (n, seg) => n[Unescape(seg)]!)
                .AsObject();
        }

        return (node, pointer);
    }

    private IEnumerable<string> Validate(string pointer, byte[] json)
    {
        if (!_schemas.TryGetValue(pointer, out var schema))
        {
            var wrapper = JsonNode.Parse(_docJson)!.AsObject();
            wrapper["$schema"] = "https://json-schema.org/draft/2020-12/schema";
            wrapper["$ref"] = "#" + pointer;
            schema = ContractSchema.FromText(wrapper.ToJsonString(), "openapi" + pointer);
            _schemas[pointer] = schema;
        }

        var verdict = schema.Validate(json);
        return verdict.Valid ? [] : [verdict.Reason];
    }

    /// <summary>A header value is text; its schema says what JSON it stands for.</summary>
    private static byte[] HeaderAsJson(JsonNode schema, string value) =>
        (string?)schema["type"] is "number" or "integer"
            ? Encoding.UTF8.GetBytes(NumberText().IsMatch(value) ? value : JsonValue.Create(value).ToJsonString())
            : Encoding.UTF8.GetBytes(JsonValue.Create(value).ToJsonString());

    private static string Escape(string segment) => segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static string Unescape(string segment) => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    /// <summary>
    /// YAML to JSON. Plain scalars are typed as YAML 1.2's core schema types them — null, booleans,
    /// integers, floats — and every quoted scalar stays a string, so <c>'null'</c> is the string the
    /// contract means and <c>null</c> is null.
    /// </summary>
    private static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode map => new JsonObject(map.Children.Select(kv =>
            KeyValuePair.Create(((YamlScalarNode)kv.Key).Value!, ToJson(kv.Value)))),
        YamlSequenceNode seq => new JsonArray(seq.Children.Select(ToJson).ToArray()),
        YamlScalarNode s when s.Style != ScalarStyle.Plain => JsonValue.Create(s.Value),
        YamlScalarNode s => s.Value switch
        {
            null or "" or "~" or "null" => null,
            "true" => JsonValue.Create(true),
            "false" => JsonValue.Create(false),
            var v when IntegerText().IsMatch(v) => JsonValue.Create(long.Parse(v, CultureInfo.InvariantCulture)),
            var v when NumberText().IsMatch(v) => JsonValue.Create(double.Parse(v, CultureInfo.InvariantCulture)),
            var v => JsonValue.Create(v),
        },
        _ => throw new NotSupportedException(node.GetType().Name),
    };

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)$")]
    private static partial Regex IntegerText();

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][-+]?[0-9]+)?$")]
    private static partial Regex NumberText();
}
