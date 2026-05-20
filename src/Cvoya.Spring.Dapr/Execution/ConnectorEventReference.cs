// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Extracts a connector event type and an external-entity reference from a
/// translated connector domain message so the platform host can stamp them
/// onto the routing-decision activity it emits after a connector event is
/// processed (issue #2560).
/// </summary>
/// <remarks>
/// <para>
/// A connector domain message is a one-way event (ADR-0048) whose
/// <see cref="Message.From"/> carries the synthetic
/// <see cref="Address.ConnectorScheme"/> provenance address. Inbound
/// translators (e.g. <c>GitHubWebhookHandler</c>) build the payload with a
/// conventional shape: a <c>source</c> string identifying the connector and
/// an <c>action</c> / <c>intent</c> naming the event. This helper reads only
/// those conventional fields — it is connector-agnostic and never
/// connector-package-specific — so the routing-decision activity carries the
/// connector event type and a stable external reference (e.g. a GitHub issue
/// number) without the coordinator depending on any connector package.
/// </para>
/// </remarks>
public readonly record struct ConnectorEventReference(
    string? EventType,
    string? EntityKind,
    string? EntityReference)
{
    /// <summary>
    /// Reads the connector event type and external-entity reference from
    /// <paramref name="message"/>'s payload. Returns an all-null reference
    /// when the payload is not a connector-shaped JSON object — callers
    /// treat that as "connector event type unknown" and still emit the
    /// routing-decision activity with whatever they could resolve.
    /// </summary>
    public static ConnectorEventReference From(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var payload = message.Payload;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        var source = ReadString(payload, "source");
        var action = ReadString(payload, "action");
        var intent = ReadString(payload, "intent");

        // Connector event type: "<source>.<action>" when both are present
        // (e.g. "github.unlabeled"), falling back to the intent or the bare
        // source so the activity always carries a non-empty label when the
        // payload identifies a connector at all.
        string? eventType = (source, action) switch
        {
            (not null, not null) => $"{source}.{action}",
            (not null, null) when intent is not null => $"{source}.{intent}",
            (not null, null) => source,
            _ => action ?? intent,
        };

        var (entityKind, entityReference) = ReadEntityReference(payload);

        return new ConnectorEventReference(eventType, entityKind, entityReference);
    }

    /// <summary>
    /// Resolves the external entity the connector event concerns — the
    /// GitHub issue / pull-request number being the canonical case. Walks
    /// the conventional <c>issue</c> / <c>pull_request</c> sub-objects and
    /// reads their <c>number</c> field.
    /// </summary>
    private static (string? Kind, string? Reference) ReadEntityReference(JsonElement payload)
    {
        // Order matters only for the rare payload that carries both — issue
        // is checked first because issue-shaped events are the dominant
        // connector traffic and the #2560 incident concerned an issue.
        foreach (var (property, kind) in EntityProbes)
        {
            if (payload.TryGetProperty(property, out var entity)
                && entity.ValueKind == JsonValueKind.Object
                && entity.TryGetProperty("number", out var number)
                && number.ValueKind == JsonValueKind.Number
                && number.TryGetInt64(out var value))
            {
                return (kind, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return (null, null);
    }

    private static readonly (string Property, string Kind)[] EntityProbes =
    [
        ("issue", "issue"),
        ("pull_request", "pull_request"),
    ];

    private static string? ReadString(JsonElement parent, string property)
        => parent.TryGetProperty(property, out var element)
            && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
