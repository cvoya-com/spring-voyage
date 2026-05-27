// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging.Rendering;

/// <summary>
/// Renders the <see cref="Message.Payload"/> JSON of a Spring Voyage
/// <see cref="Message"/> as plain text, for consumers that need a
/// single-string body view of a structured envelope: the conversation
/// timeline / inbox, the Slack outbound dispatcher, and any future
/// connector that posts text to an external surface.
/// </summary>
/// <remarks>
/// <para>
/// Renderers are pure: <see cref="CanRender(Message)"/> and
/// <see cref="Render(Message)"/> only read <paramref name="message"/> and
/// must not perform I/O or hold state across calls. The registry can call
/// <c>CanRender</c> concurrently for different messages.
/// </para>
/// <para>
/// Selection contract — the registry filters by <see cref="TargetType"/>
/// first (a <c>null</c> target matches any <see cref="MessageType"/>),
/// then walks remaining renderers in descending <see cref="Priority"/>
/// order and picks the first one whose <see cref="CanRender(Message)"/>
/// returns <c>true</c>. The order is deterministic; multiple renderers
/// claiming the same shape produce the same winner for the same message.
/// </para>
/// </remarks>
public interface IMessagePayloadRenderer
{
    /// <summary>
    /// The <see cref="MessageType"/> this renderer is willing to render,
    /// or <c>null</c> when the renderer is type-agnostic and depends only
    /// on payload shape. The registry uses this as a pre-filter before
    /// calling <see cref="CanRender(Message)"/>.
    /// </summary>
    MessageType? TargetType { get; }

    /// <summary>
    /// Tie-break priority used by the registry when multiple renderers'
    /// <see cref="TargetType"/> matches. Higher wins. The built-in
    /// renderers occupy a narrow band; cloud overlays that need to
    /// override a built-in claim a higher number.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Returns <c>true</c> when this renderer recognises
    /// <paramref name="message"/>'s payload shape and is willing to
    /// produce a rendered string. The registry calls this in priority
    /// order and stops at the first <c>true</c>.
    /// </summary>
    bool CanRender(Message message);

    /// <summary>
    /// Renders the message's payload as plain text. Only called by the
    /// registry when <see cref="CanRender(Message)"/> returned
    /// <c>true</c>. Returning <c>null</c> from <see cref="Render"/>
    /// signals the registry to fall through to the next eligible
    /// renderer (the renderer changed its mind), so the regular shape is
    /// to return a non-null string.
    /// </summary>
    string? Render(Message message);
}
