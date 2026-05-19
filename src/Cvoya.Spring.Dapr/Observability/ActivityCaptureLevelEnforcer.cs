// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using System.Text.Json;
using System.Text.Json.Nodes;

using Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Applies the tenant's <see cref="ActivityCaptureLevel"/> to a captured
/// activity payload. Issue #2492.
/// </summary>
/// <remarks>
/// <para>
/// Enforcement runs <strong>server-side at ingest</strong>: the runtime
/// always emits full payloads; this enforcer trims or drops fields
/// based on the tenant's setting. At <see cref="ActivityCaptureLevel.Off"/>
/// callers should not invoke <see cref="Apply"/> — the ingest pipeline
/// short-circuits before reaching this stage.
/// </para>
/// <para>
/// The truncation contract for <see cref="ActivityCaptureLevel.Summary"/>:
/// every string value at or below <see cref="MaxStringLength"/> survives
/// verbatim. Longer strings are replaced with the first
/// <see cref="HeadCharacters"/> characters, an ellipsis marker, and the
/// last <see cref="TailCharacters"/> characters; a sibling
/// <c>truncated: true</c> key is stamped on the parent object so the
/// portal / CLI can render a "truncated" hint without re-deriving it
/// from the byte length.
/// </para>
/// </remarks>
public static class ActivityCaptureLevelEnforcer
{
    /// <summary>Maximum length, in characters, a string may keep at <c>summary</c> before truncation.</summary>
    public const int MaxStringLength = 1024;

    /// <summary>Characters retained from the head of an over-long string at <c>summary</c>.</summary>
    public const int HeadCharacters = 512;

    /// <summary>Characters retained from the tail of an over-long string at <c>summary</c>.</summary>
    public const int TailCharacters = 512;

    private const string TruncationMarker = " […] ";

    /// <summary>
    /// Returns a new <see cref="JsonElement"/> with truncation rules
    /// applied per <paramref name="level"/>. The <c>Off</c> case throws
    /// — callers must short-circuit before reaching this path.
    /// </summary>
    /// <param name="payload">The redacted-but-untruncated details payload.</param>
    /// <param name="level">The tenant's capture level.</param>
    public static JsonElement Apply(JsonElement payload, ActivityCaptureLevel level)
    {
        switch (level)
        {
            case ActivityCaptureLevel.Off:
                throw new InvalidOperationException(
                    "ActivityCaptureLevelEnforcer.Apply called with Off; the ingest pipeline must drop events before reaching this stage.");
            case ActivityCaptureLevel.Full:
                return payload;
            case ActivityCaptureLevel.Summary:
                var node = JsonNode.Parse(payload.GetRawText());
                if (node is JsonObject obj)
                {
                    TruncateObject(obj);
                }
                else if (node is JsonArray arr)
                {
                    TruncateArray(arr);
                }
                else if (node is JsonValue val && val.TryGetValue<string>(out var raw) && raw.Length > MaxStringLength)
                {
                    return JsonSerializer.SerializeToElement(Truncate(raw));
                }
                return JsonSerializer.SerializeToElement(node);
            default:
                throw new ArgumentOutOfRangeException(nameof(level), level, null);
        }
    }

    private static void TruncateObject(JsonObject obj)
    {
        var truncatedAny = false;
        foreach (var key in obj.Select(kv => kv.Key).ToList())
        {
            var value = obj[key];
            switch (value)
            {
                case JsonValue jv when jv.TryGetValue<string>(out var s) && s.Length > MaxStringLength:
                    obj[key] = JsonValue.Create(Truncate(s));
                    truncatedAny = true;
                    break;
                case JsonObject child:
                    TruncateObject(child);
                    break;
                case JsonArray childArr:
                    TruncateArray(childArr);
                    break;
            }
        }

        if (truncatedAny && !obj.ContainsKey("truncated"))
        {
            obj["truncated"] = JsonValue.Create(true);
        }
    }

    private static void TruncateArray(JsonArray arr)
    {
        for (var i = 0; i < arr.Count; i++)
        {
            switch (arr[i])
            {
                case JsonValue jv when jv.TryGetValue<string>(out var s) && s.Length > MaxStringLength:
                    arr[i] = JsonValue.Create(Truncate(s));
                    break;
                case JsonObject child:
                    TruncateObject(child);
                    break;
                case JsonArray childArr:
                    TruncateArray(childArr);
                    break;
            }
        }
    }

    private static string Truncate(string raw)
        => string.Concat(
            raw.AsSpan(0, HeadCharacters),
            TruncationMarker,
            raw.AsSpan(raw.Length - TailCharacters, TailCharacters));
}
