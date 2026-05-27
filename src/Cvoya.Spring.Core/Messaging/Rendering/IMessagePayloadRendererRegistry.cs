// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging.Rendering;

/// <summary>
/// Resolves the canonical plain-text rendering of a Spring Voyage
/// <see cref="Message"/>'s payload by walking the registered
/// <see cref="IMessagePayloadRenderer"/> set. The registry is the single
/// consumer-facing surface — call sites that need "what does this message
/// look like as text?" go through this interface so the heuristic does
/// not drift between the conversation timeline, the Slack outbound
/// dispatcher, and future connector surfaces (#2843).
/// </summary>
public interface IMessagePayloadRendererRegistry
{
    /// <summary>
    /// Returns the text rendering of <paramref name="message"/>'s
    /// payload, or <c>null</c> when no registered renderer claims the
    /// payload shape. Callers decide what to do with the <c>null</c>
    /// case: the conversation timeline drops the <c>body</c> field; the
    /// Slack outbound dispatcher falls back to the raw JSON so the
    /// recipient still sees something rather than nothing.
    /// </summary>
    string? TryRender(Message message);
}
