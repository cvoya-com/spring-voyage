// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging.Rendering.Renderers;

using System.Text.Json;

/// <summary>
/// Base for the object-shape renderers — recognises payloads that are a
/// JSON object carrying a single named string property at the top level.
/// </summary>
/// <remarks>
/// Each Spring Voyage producer surface wraps text into a different
/// well-known property: <c>text</c> / <c>body</c> for external/connector
/// shapes, <c>Output</c> for the dispatcher's agent-reply wrap (#1547,
/// #1549), <c>content</c> for the <c>sv.messaging.send</c> wrap (#2767).
/// The renderer is intentionally narrow — only top-level string
/// properties on a JSON object qualify; nested shapes, arrays, and
/// non-string values fall through to the next renderer or to the
/// caller's null-handling.
/// </remarks>
public abstract class StringPropertyPayloadRenderer : IMessagePayloadRenderer
{
    /// <summary>
    /// The JSON property name to look for on the top-level payload object.
    /// </summary>
    protected abstract string PropertyName { get; }

    /// <inheritdoc />
    public virtual MessageType? TargetType => null;

    /// <inheritdoc />
    public abstract int Priority { get; }

    /// <inheritdoc />
    public bool CanRender(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return message.Payload.TryGetProperty(PropertyName, out var prop)
            && prop.ValueKind == JsonValueKind.String;
    }

    /// <inheritdoc />
    public string? Render(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return message.Payload.TryGetProperty(PropertyName, out var prop)
            && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }
}

/// <summary>
/// Renders payloads shaped as <c>{ "text": "..." }</c>. This is the
/// agent-author-facing convention — what a human or agent intuitively
/// names a text field. Slack's outbound dispatcher recognised this shape
/// pre-#2843 (<see cref="StringPropertyPayloadRenderer"/> docs).
/// </summary>
public sealed class TextPropertyPayloadRenderer : StringPropertyPayloadRenderer
{
    /// <inheritdoc />
    protected override string PropertyName => "text";

    /// <inheritdoc />
    public override int Priority => 80;
}

/// <summary>
/// Renders payloads shaped as <c>{ "body": "..." }</c>. Alternate
/// human-author convention (HTTP/email-style). Slack's outbound
/// dispatcher recognised this shape pre-#2843.
/// </summary>
public sealed class BodyPropertyPayloadRenderer : StringPropertyPayloadRenderer
{
    /// <inheritdoc />
    protected override string PropertyName => "body";

    /// <inheritdoc />
    public override int Priority => 70;
}

/// <summary>
/// Renders payloads shaped as <c>{ "Output": "...", "ExitCode": ... }</c>
/// — the dispatcher's agent-reply wrap that
/// <see cref="MessageType.Domain"/> messages from
/// <c>A2AExecutionDispatcher</c> carry (#1547, #1549). The capitalisation
/// matches the C# DTO property name; the platform writes this shape
/// from .NET so the JSON serialiser preserves casing.
/// </summary>
public sealed class OutputPropertyPayloadRenderer : StringPropertyPayloadRenderer
{
    /// <inheritdoc />
    protected override string PropertyName => "Output";

    /// <inheritdoc />
    public override int Priority => 60;
}

/// <summary>
/// Renders payloads shaped as <c>{ "content": "..." }</c> — produced by
/// <c>SvMessagingSkillRegistry.ExtractMessagePayload</c> when an agent
/// calls <c>sv.messaging.send</c> with a string <c>message</c> argument
/// (#2767). Without this renderer, the unit-or-agent reply persisted by
/// #2764 would surface as an empty bubble in the inbox and thread
/// timeline.
/// </summary>
public sealed class ContentPropertyPayloadRenderer : StringPropertyPayloadRenderer
{
    /// <inheritdoc />
    protected override string PropertyName => "content";

    /// <inheritdoc />
    public override int Priority => 50;
}
