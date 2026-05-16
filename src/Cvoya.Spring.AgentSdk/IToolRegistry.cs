// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

using System.Text.Json;

using Cvoya.Spring.Core.Skills;

/// <summary>
/// Handler signature for an SDK-registered tool.
/// </summary>
/// <param name="arguments">
/// The JSON arguments supplied by the caller. Validated by the runtime
/// against the tool's <see cref="ToolDefinition.InputSchema"/> before
/// dispatch — the handler does NOT need to re-validate.
/// </param>
/// <param name="cancellationToken">A token used to cancel the tool call.</param>
/// <returns>
/// The JSON result. The runtime serialises this back to the caller without
/// re-shaping; producers should match whatever output contract the tool
/// promised in its declared schema.
/// </returns>
public delegate Task<JsonElement> ToolHandler(
    JsonElement arguments,
    CancellationToken cancellationToken);

/// <summary>
/// In-process registry that agent authors populate to expose their
/// container-image-tier tools (sub-C of #2332).
/// </summary>
/// <remarks>
/// <para>
/// The registry is the agent-author-facing seam: each
/// <see cref="Register"/> call attaches a <see cref="ToolDefinition"/>
/// (the wire shape the platform persists on <c>image_tools</c>) plus the
/// handler that runs when the platform dispatches a tool call back into
/// the agent. The handler lives only in the agent process; the platform
/// never sees it, never serialises it, and never persists it.
/// </para>
/// <para>
/// Every registered tool id MUST match
/// <see cref="ToolNaming.Pattern"/> — both the <see cref="ToolDefinition"/>
/// constructor and the registry re-check at registration time so a
/// mis-formed id fails loudly at startup rather than silently shipping a
/// non-canonical surface (#2334 / Sub A).
/// </para>
/// <para>
/// Duplicate names are rejected so two registrations cannot fight over
/// the same id. Iteration order is deterministic — registrations come
/// back in insertion order so the <c>/a2a/tools</c> endpoint produces a
/// stable, diff-able payload.
/// </para>
/// </remarks>
public interface IToolRegistry
{
    /// <summary>
    /// Registers <paramref name="definition"/> with the given
    /// <paramref name="handler"/>. Throws when the id is non-canonical or
    /// already registered.
    /// </summary>
    /// <param name="definition">
    /// The tool's wire definition. The constructor of
    /// <see cref="ToolDefinition"/> enforces
    /// <see cref="ToolNaming.Pattern"/> on the way in; this method does
    /// not re-throw for that reason — invalid ids never reach it.
    /// </param>
    /// <param name="handler">
    /// The in-process handler invoked when the agent receives a tool call
    /// for <paramref name="definition"/>.<see cref="ToolDefinition.Name"/>.
    /// MUST NOT be <c>null</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="handler"/> is
    /// <c>null</c>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A tool with the same <see cref="ToolDefinition.Name"/> has already
    /// been registered.
    /// </exception>
    void Register(ToolDefinition definition, ToolHandler handler);

    /// <summary>
    /// Returns the registered <see cref="ToolDefinition"/>s in
    /// registration order. Empty when no tools are registered.
    /// </summary>
    IReadOnlyList<ToolDefinition> List();

    /// <summary>
    /// Returns the handler associated with <paramref name="name"/>, or
    /// <c>null</c> when no handler was registered for it.
    /// </summary>
    ToolHandler? GetHandler(string name);
}
