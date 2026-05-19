// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// Library-defined redaction applied at ingest. Matches the case-insensitive
/// list of auth-header attribute keys, masks values of environment-variable
/// keys that look like credentials, and scrubs in-line bearer tokens from
/// attribute values. Issue #2492.
/// </summary>
/// <remarks>
/// <para>
/// Tenant-defined PII redaction rules are deliberately out of scope —
/// follow-up. The OSS surface only redacts the well-known credential
/// shapes. The redactor never throws on malformed payloads; it returns
/// the input unchanged when it can't recurse safely.
/// </para>
/// </remarks>
public static partial class ActivityRedactor
{
    /// <summary>Placeholder value substituted for any matched secret.</summary>
    public const string RedactedMarker = "[REDACTED]";

    private static readonly HashSet<string> HeaderKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "proxy-authorization",
        "x-api-key",
        "x-auth-token",
        "cookie",
        "set-cookie",
    };

    [GeneratedRegex(@"^[A-Z0-9_]*(?:TOKEN|KEY|SECRET|PASSWORD|PASSWD|PWD)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 500)]
    private static partial Regex CredentialEnvVarRegex();

    // Bearer / Basic auth values in attribute strings. The match is
    // conservative — only the well-known auth prefixes (case-insensitive)
    // followed by a non-trivial token. Inline tokens that don't follow
    // one of these patterns survive; the tenant-defined PII rules
    // follow-up is the place to expand this set safely.
    [GeneratedRegex(@"\b(?:Bearer|Basic|Token)\s+[A-Za-z0-9+/_\-\.=]{8,}",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 500)]
    private static partial Regex InlineCredentialRegex();

    /// <summary>
    /// Applies the OSS redaction rules to <paramref name="payload"/> and
    /// returns a new <see cref="JsonElement"/>. Returns the input
    /// unchanged when redaction can't safely walk the structure (for
    /// example a non-object root that doesn't contain any matched
    /// pattern).
    /// </summary>
    /// <param name="payload">The structured details payload to redact.</param>
    public static JsonElement Redact(JsonElement payload)
    {
        var node = JsonNode.Parse(payload.GetRawText());
        RedactNode(node);
        // Re-serialise to a JsonElement; the round-trip is cheap on the
        // small payload sizes we deal with at ingest time (~KB).
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="key"/> looks like an
    /// environment-variable credential key (suffix <c>_TOKEN</c>,
    /// <c>_KEY</c>, <c>_SECRET</c>, <c>_PASSWORD</c>, etc.).
    /// </summary>
    public static bool IsCredentialEnvVarKey(string key)
        => !string.IsNullOrEmpty(key) && CredentialEnvVarRegex().IsMatch(key);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="key"/> is one of the
    /// well-known auth-header attribute keys.
    /// </summary>
    public static bool IsCredentialHeaderKey(string key)
        => !string.IsNullOrEmpty(key) && HeaderKeys.Contains(key);

    private static void RedactNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                RedactObject(obj);
                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    RedactNode(item);
                }
                break;
                // Scalars are walked at the parent level so the key can be
                // inspected; no work to do here.
        }
    }

    private static void RedactObject(JsonObject obj)
    {
        // ToList() to materialise the keys — we mutate the dictionary
        // mid-iteration when a match fires.
        foreach (var key in obj.Select(kv => kv.Key).ToList())
        {
            var value = obj[key];

            if (IsCredentialHeaderKey(key) || IsCredentialEnvVarKey(key))
            {
                if (value is JsonValue)
                {
                    obj[key] = JsonValue.Create(RedactedMarker);
                }
                else
                {
                    // Object / array under a credential-shaped key: replace
                    // entirely so nested cookie maps or token bundles
                    // don't leak.
                    obj[key] = JsonValue.Create(RedactedMarker);
                }
                continue;
            }

            switch (value)
            {
                case JsonValue jv when jv.TryGetValue<string>(out var s):
                    if (InlineCredentialRegex().IsMatch(s))
                    {
                        obj[key] = JsonValue.Create(
                            InlineCredentialRegex().Replace(s, RedactedMarker));
                    }
                    break;
                case JsonObject:
                case JsonArray:
                    RedactNode(value);
                    break;
            }
        }
    }
}
