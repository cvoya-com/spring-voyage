// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Contract;

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

using Json.Schema;

/// <summary>
/// Loads the committed <c>src/Cvoya.Spring.Host.Api/openapi.json</c> once
/// and exposes per-endpoint <see cref="JsonSchema"/> instances so contract
/// tests can validate response bodies against the public contract. Companion
/// to the <c>openapi-drift</c> CI job (#1248): drift catches *route-level*
/// changes; this helper lets tests catch *semantic* drift — required→optional,
/// status-code reshuffles, error-envelope shape changes.
/// </summary>
/// <remarks>
/// <para>
/// OpenAPI 3.1 component schemas are real JSON Schema 2020-12, so we feed
/// them straight into <c>JsonSchema.Net</c>. The whole openapi.json is
/// registered with the schema registry under a stable base URI; per-test
/// schema lookups return a tiny wrapper schema whose only keyword is a
/// <c>$ref</c> back into that base document, so <c>$ref</c>s inside
/// component schemas resolve transparently.
/// </para>
/// <para>
/// One static instance is shared across all contract tests — the openapi.json
/// is large (≈12k lines) and parsing it is non-trivial. Tests treat the
/// instance as read-only.
/// </para>
/// </remarks>
public static class OpenApiContract
{
    private const string BaseUri = "https://contract-tests.spring-voyage.local/openapi.json";

    private static readonly Lazy<OpenApiDocument> _document = new(LoadDocument, isThreadSafe: true);

    private static readonly ConcurrentDictionary<string, JsonSchema> _refSchemaCache = new();

    /// <summary>
    /// Validate <paramref name="bodyJson"/> against the response schema
    /// declared on <c>paths.{path}.{method}.responses.{statusCode}.content.{mediaType}.schema</c>.
    /// Throws <see cref="ContractAssertionException"/> on failure.
    /// </summary>
    /// <param name="path">The OpenAPI path template, e.g. <c>/api/v1/agents</c>.</param>
    /// <param name="method">The HTTP method, e.g. <c>get</c>.</param>
    /// <param name="statusCode">The status code as it appears in the spec, e.g. <c>"200"</c>.</param>
    /// <param name="bodyJson">The raw response body JSON.</param>
    /// <param name="mediaType">
    /// The response media type. Defaults to <c>application/json</c>; pass
    /// <c>application/problem+json</c> for the RFC7807 error path.
    /// </param>
    public static void AssertResponse(
        string path,
        string method,
        string statusCode,
        string bodyJson,
        string mediaType = "application/json")
    {
        var schema = GetResponseSchema(path, method, statusCode, mediaType);
        // Hold the JsonDocument until evaluation finishes — its
        // RootElement view is what JsonSchema.Net.Evaluate consumes.
        using var instanceDoc = ParseInstance(bodyJson);
        var results = schema.Evaluate(instanceDoc.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        if (!results.IsValid)
        {
            throw new ContractAssertionException(
                BuildFailureMessage(path, method, statusCode, mediaType, bodyJson, results));
        }
    }

    /// <summary>
    /// Validate that the spec actually declares a response for
    /// <c>{path}.{method}.{statusCode}</c>. Useful as a regression guard:
    /// if a status-code branch is removed from the spec, every test that
    /// asserts that branch must fail loudly rather than silently skipping.
    /// </summary>
    public static void AssertStatusDeclared(string path, string method, string statusCode)
    {
        var doc = _document.Value;
        if (!TryResolveStatus(doc.Root, path, method, statusCode, out _))
        {
            throw new ContractAssertionException(
                $"OpenAPI contract does not declare {method.ToUpperInvariant()} {path} -> {statusCode}.");
        }
    }

    /// <summary>
    /// Returns true when the spec declares the given response media type
    /// for the path/method/status; false otherwise. Used by tests that
    /// want to assert "this 204 response carries no body" — schema-less
    /// branches in the spec.
    /// </summary>
    public static bool DeclaresContent(string path, string method, string statusCode)
    {
        var doc = _document.Value;
        if (!TryResolveStatus(doc.Root, path, method, statusCode, out var status))
        {
            return false;
        }
        if (!status.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        // EnumerateObject().Any() — content is a map of media-type → object.
        var enumerator = content.EnumerateObject();
        return enumerator.MoveNext();
    }

    private static JsonSchema GetResponseSchema(
        string path,
        string method,
        string statusCode,
        string mediaType)
    {
        var key = $"{method}:{path}:{statusCode}:{mediaType}";
        return _refSchemaCache.GetOrAdd(key, _ =>
        {
            var doc = _document.Value;
            if (!TryResolveStatus(doc.Root, path, method, statusCode, out var status))
            {
                throw new ContractAssertionException(
                    $"OpenAPI contract does not declare {method.ToUpperInvariant()} {path} -> {statusCode}.");
            }

            if (!status.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Object)
            {
                throw new ContractAssertionException(
                    $"{method.ToUpperInvariant()} {path} -> {statusCode} declares no response body.");
            }

            if (!content.TryGetProperty(mediaType, out var media)
                || media.ValueKind != JsonValueKind.Object)
            {
                throw new ContractAssertionException(
                    $"{method.ToUpperInvariant()} {path} -> {statusCode} does not declare media type '{mediaType}'.");
            }

            // Build a thin wrapper schema whose only keyword is a $ref into
            // the registered base document. JsonSchema.Net resolves the $ref
            // through the SchemaRegistry, evaluating the referenced subschema
            // — including any nested $refs — against the instance.
            //
            // Two-step escaping per the URI fragment + JSON Pointer specs:
            //   1. RFC6901 (JSON Pointer): '~' -> '~0', '/' -> '~1'. Without
            //      this, segments like 'application/json' would terminate
            //      the pointer mid-segment.
            //   2. RFC3986 (URI fragment): URI-encode reserved characters
            //      that survived step 1. '+' (in 'application/problem+json')
            //      and '{' / '}' (in path templates like '/agents/{id}')
            //      otherwise corrupt the URI fragment and JsonSchema.Net's
            //      RefResolutionException is the result.
            var pointer = string.Join('/', new[]
            {
                "paths",
                EscapePointerSegmentForUri(path),
                method,
                "responses",
                statusCode,
                "content",
                EscapePointerSegmentForUri(mediaType),
                "schema",
            });
            var refUri = $"{BaseUri}#/{pointer}";

            var schemaJson = $$"""{ "$ref": "{{refUri}}" }""";
            return JsonSchema.FromText(schemaJson);
        });
    }

    private static bool TryResolveStatus(
        JsonElement root,
        string path,
        string method,
        string statusCode,
        out JsonElement status)
    {
        status = default;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("paths", out var paths)
            || paths.ValueKind != JsonValueKind.Object) return false;
        if (!paths.TryGetProperty(path, out var pathItem)
            || pathItem.ValueKind != JsonValueKind.Object) return false;
        if (!pathItem.TryGetProperty(method, out var operation)
            || operation.ValueKind != JsonValueKind.Object) return false;
        if (!operation.TryGetProperty("responses", out var responses)
            || responses.ValueKind != JsonValueKind.Object) return false;
        if (!responses.TryGetProperty(statusCode, out var s)
            || s.ValueKind != JsonValueKind.Object) return false;
        status = s;
        return true;
    }

    private static OpenApiDocument LoadDocument()
    {
        var path = ResolveOpenApiPath();
        var bytes = File.ReadAllBytes(path);
        // Hold the raw bytes too — JsonElement clones into a JsonDocument
        // that we keep alive for the lifetime of the contract registry. The
        // registry references the JsonElement; disposing the document would
        // invalidate every cached schema.
        var jsonDoc = JsonDocument.Parse(bytes);

        // Register the document with JsonSchema.Net so $ref resolution into
        // it works from per-test wrapper schemas. The base document doubles
        // as both the OpenAPI doc and the JSON Schema $defs container —
        // OpenAPI 3.1 component schemas are real JSON Schema 2020-12 so
        // this is sound.
        var baseUri = new Uri(BaseUri);
        SchemaRegistry.Global.Register(
            baseUri,
            new JsonElementBaseDocument(jsonDoc.RootElement, baseUri));

        return new OpenApiDocument(jsonDoc);
    }

    private static string ResolveOpenApiPath()
    {
        // The csproj copies the committed openapi.json into the test output
        // directory under the same name. AppContext.BaseDirectory is the
        // test bin folder at runtime.
        var local = Path.Combine(AppContext.BaseDirectory, "openapi.json");
        if (File.Exists(local))
        {
            return local;
        }

        // Fallback: walk up until we find AGENTS.md (the repo-root marker
        // used by other tests). Robust against a stale build that didn't
        // copy the file in — the contract is still authoritative.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            {
                var candidate = Path.Combine(dir.FullName, "src", "Cvoya.Spring.Host.Api", "openapi.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate openapi.json. Expected it next to the test binary "
            + "(copied via the test csproj) or under src/Cvoya.Spring.Host.Api/ "
            + "from a repo-root walk.");
    }

    private static JsonDocument ParseInstance(string bodyJson)
    {
        if (string.IsNullOrEmpty(bodyJson))
        {
            // JSON null is a valid instance for nullable schemas; an empty
            // string is not parsable. Surface a clear error rather than the
            // System.Text.Json parser complaint.
            throw new ContractAssertionException(
                "Response body is empty; cannot validate against contract.");
        }
        try
        {
            return JsonDocument.Parse(bodyJson);
        }
        catch (JsonException ex)
        {
            throw new ContractAssertionException(
                $"Response body is not valid JSON: {ex.Message}", ex);
        }
    }

    private static string EscapePointerSegmentForUri(string segment)
    {
        // JSON Pointer escape (RFC6901) first, then URI-encode the result
        // so reserved-in-fragment characters ('+', '{', '}') don't corrupt
        // the wrapper schema's $ref URI.
        var pointerEscaped = segment.Replace("~", "~0").Replace("/", "~1");
        return Uri.EscapeDataString(pointerEscaped);
    }

    private static string BuildFailureMessage(
        string path,
        string method,
        string statusCode,
        string mediaType,
        string bodyJson,
        EvaluationResults results)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"Response body for {method.ToUpperInvariant()} {path} -> {statusCode} ({mediaType}) does not match the OpenAPI contract.");
        sb.AppendLine();
        sb.AppendLine("Body:");
        sb.AppendLine(bodyJson);
        sb.AppendLine("Schema validation errors:");
        AppendErrors(sb, results, depth: 0);
        return sb.ToString();
    }

    private static void AppendErrors(System.Text.StringBuilder sb, EvaluationResults results, int depth)
    {
        if (results.IsValid)
        {
            return;
        }
        var indent = new string(' ', depth * 2);
        if (results.Errors is { Count: > 0 } errors)
        {
            foreach (var (keyword, message) in errors)
            {
                sb.Append(indent).Append("- ").Append(results.InstanceLocation).Append(' ')
                  .Append('[').Append(keyword).Append("] ").AppendLine(message);
            }
        }
        if (results.Details is { Count: > 0 } details)
        {
            foreach (var detail in details)
            {
                AppendErrors(sb, detail, depth + 1);
            }
        }
    }

    /// <summary>
    /// Holds the parsed OpenAPI document. The <see cref="JsonDocument"/> is
    /// kept alive for the lifetime of the contract — the global
    /// <see cref="SchemaRegistry"/> retains a reference into its
    /// <see cref="JsonElement"/> root for $ref resolution.
    /// </summary>
    private sealed class OpenApiDocument(JsonDocument doc)
    {
#pragma warning disable IDE0052 // Held to keep the JsonElement Root alive.
        private readonly JsonDocument _doc = doc;
#pragma warning restore IDE0052
        public JsonElement Root { get; } = doc.RootElement;
    }
}

/// <summary>
/// Thrown when a response body does not match its declared OpenAPI contract.
/// Tests catch failures by letting this propagate; the message includes the
/// failing instance and a flattened error trail.
/// </summary>
public sealed class ContractAssertionException : Exception
{
    public ContractAssertionException(string message) : base(message) { }

    public ContractAssertionException(string message, Exception inner) : base(message, inner) { }
}