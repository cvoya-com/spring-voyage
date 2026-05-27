// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging.Rendering.Renderers;

using System.Text;
using System.Text.Json;

/// <summary>
/// Renders payloads produced by <c>A2AExecutionDispatcher</c> when it wraps
/// an A2A response (<c>AgentTask</c> or <c>AgentMessage</c>) as a
/// <see cref="Message.Payload"/> for the renderer registry (#2856).
/// </summary>
/// <remarks>
/// <para>
/// Pinned payload shape — the dispatcher serialises the A2A response with
/// the SDK's own JSON layout and replaces the top-level <c>kind</c>
/// discriminator with one of two namespaced values:
/// </para>
/// <list type="bullet">
///   <item><c>kind = "a2a.task"</c> — full task envelope; walked in
///     priority order over <c>artifacts</c> → <c>status.message.parts</c>
///     → <c>history</c> (the last entry whose <c>role</c> is <c>"agent"</c>).
///     This mirrors the pre-#2856 in-file <c>ExtractTextFromTask</c>
///     helper one-for-one.</item>
///   <item><c>kind = "a2a.message"</c> — bare <c>parts</c> array on the
///     payload's top level. The dispatcher's <c>AgentMessage</c> branch
///     never produces artifacts/history, so the renderer walks
///     <c>parts</c> directly.</item>
/// </list>
/// <para>
/// Part extraction is uniform across all three sites: every <c>parts</c>
/// array is filtered to entries whose <c>kind</c> is <c>"text"</c> (the
/// A2A SDK's <c>TextPart</c> discriminator), and the <c>text</c> field of
/// each is concatenated with <c>'\n'</c> separators. Non-text parts
/// (<c>FilePart</c>, <c>DataPart</c>) are dropped — the platform message
/// protocol still only carries plain text today, matching the pre-#2856
/// helper's <c>OfType&lt;TextPart&gt;</c> filter.
/// </para>
/// <para>
/// Design note — the <c>kind</c> field on the payload looks like the
/// "explicit <c>payload.kind</c> discriminator" alternative
/// <see href="../../../../../docs/decisions/0063-message-payload-renderer-registry.md">ADR-0063</see>
/// rejected for platform-wide use, but the rejection was scoped to
/// "every producer self-declares" — external producers (Slack / GitHub
/// webhook events, future connectors) cannot follow that convention.
/// Here the dispatcher is a Spring-Voyage-internal producer that wraps
/// an external A2A response at the platform boundary; the
/// namespaced <c>a2a.*</c> kind is what that wrap looks like. The ADR
/// explicitly notes "wrap them at the boundary regardless" as the
/// expected pattern. The shape is durable because we own the wrap.
/// </para>
/// </remarks>
public sealed class A2aTaskPayloadRenderer : IMessagePayloadRenderer
{
    /// <summary>The <c>kind</c> value the dispatcher stamps on a wrapped <c>AgentTask</c>.</summary>
    public const string TaskKind = "a2a.task";

    /// <summary>The <c>kind</c> value the dispatcher stamps on a wrapped <c>AgentMessage</c>.</summary>
    public const string MessageKind = "a2a.message";

    private const string KindProperty = "kind";
    private const string ArtifactsProperty = "artifacts";
    private const string StatusProperty = "status";
    private const string MessageProperty = "message";
    private const string HistoryProperty = "history";
    private const string PartsProperty = "parts";
    private const string RoleProperty = "role";
    private const string TextProperty = "text";
    private const string TextKindValue = "text";
    private const string AgentRoleValue = "agent";

    /// <inheritdoc />
    public MessageType? TargetType => null;

    /// <inheritdoc />
    /// <remarks>
    /// Sits below every other built-in renderer (text=80, body=70,
    /// Output=60, content=50). The A2A shape is namespaced via
    /// <c>kind</c>, so it never collides with the well-known single-string
    /// shapes — the band is unconstrained but kept tidy so cloud overlays
    /// can slot custom renderers above or below without renumbering.
    /// </remarks>
    public int Priority => 40;

    /// <inheritdoc />
    public bool CanRender(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!message.Payload.TryGetProperty(KindProperty, out var kind)
            || kind.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = kind.GetString();
        return value is TaskKind or MessageKind;
    }

    /// <inheritdoc />
    public string? Render(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Payload.ValueKind != JsonValueKind.Object
            || !message.Payload.TryGetProperty(KindProperty, out var kind)
            || kind.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return kind.GetString() switch
        {
            TaskKind => RenderTask(message.Payload),
            MessageKind => RenderMessage(message.Payload),
            _ => null,
        };
    }

    private static string? RenderTask(JsonElement payload)
    {
        // Priority order mirrors pre-#2856 ExtractTextFromTask: artifacts win
        // when they carry any text; otherwise fall through to the status
        // message; otherwise to the last agent-role entry in history. A
        // present-but-empty layer falls through to the next (e.g. an
        // artifact array with only non-text parts).
        if (payload.TryGetProperty(ArtifactsProperty, out var artifacts)
            && artifacts.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var artifact in artifacts.EnumerateArray())
            {
                if (artifact.ValueKind == JsonValueKind.Object
                    && artifact.TryGetProperty(PartsProperty, out var parts))
                {
                    AppendTextFromParts(sb, parts);
                }
            }
            if (sb.Length > 0)
            {
                return sb.ToString();
            }
        }

        if (payload.TryGetProperty(StatusProperty, out var status)
            && status.ValueKind == JsonValueKind.Object
            && status.TryGetProperty(MessageProperty, out var statusMessage)
            && statusMessage.ValueKind == JsonValueKind.Object
            && statusMessage.TryGetProperty(PartsProperty, out var statusParts))
        {
            var sb = new StringBuilder();
            AppendTextFromParts(sb, statusParts);
            if (sb.Length > 0)
            {
                return sb.ToString();
            }
        }

        if (payload.TryGetProperty(HistoryProperty, out var history)
            && history.ValueKind == JsonValueKind.Array)
        {
            JsonElement? lastAgent = null;
            foreach (var entry in history.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty(RoleProperty, out var role)
                    && role.ValueKind == JsonValueKind.String
                    && string.Equals(role.GetString(), AgentRoleValue, StringComparison.Ordinal))
                {
                    lastAgent = entry;
                }
            }

            if (lastAgent is { } agent
                && agent.TryGetProperty(PartsProperty, out var agentParts))
            {
                var sb = new StringBuilder();
                AppendTextFromParts(sb, agentParts);
                if (sb.Length > 0)
                {
                    return sb.ToString();
                }
            }
        }

        return null;
    }

    private static string? RenderMessage(JsonElement payload)
    {
        if (!payload.TryGetProperty(PartsProperty, out var parts))
        {
            return null;
        }

        var sb = new StringBuilder();
        AppendTextFromParts(sb, parts);
        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static void AppendTextFromParts(StringBuilder sb, JsonElement parts)
    {
        if (parts.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!part.TryGetProperty(KindProperty, out var partKind)
                || partKind.ValueKind != JsonValueKind.String
                || !string.Equals(partKind.GetString(), TextKindValue, StringComparison.Ordinal))
            {
                continue;
            }

            if (!part.TryGetProperty(TextProperty, out var text)
                || text.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(text.GetString());
        }
    }
}
