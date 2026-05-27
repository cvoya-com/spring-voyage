// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging.Rendering.Renderers;

using System.Text.Json;

/// <summary>
/// Renders payloads that are a bare JSON string verbatim. The
/// <c>spring message send</c> CLI path and the <c>ThreadMessageRequest</c>
/// HTTP path both wrap the caller's text as <c>UntypedString</c>, so the
/// payload arrives as a JSON string at the receiver. Returning the
/// string unchanged is the canonical rendering.
/// </summary>
public sealed class BareStringPayloadRenderer : IMessagePayloadRenderer
{
    /// <inheritdoc />
    public MessageType? TargetType => null;

    /// <inheritdoc />
    public int Priority => 100;

    /// <inheritdoc />
    public bool CanRender(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Payload.ValueKind == JsonValueKind.String;
    }

    /// <inheritdoc />
    public string? Render(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Payload.GetString();
    }
}
