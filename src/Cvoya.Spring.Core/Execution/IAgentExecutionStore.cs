// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Read/write seam for the agent's persisted <c>execution:</c> block on
/// the <c>AgentDefinitions.Definition</c> JSON (#601 / #603 / #409
/// B-wide). Exposes the same four-field shape as <see cref="IUnitExecutionStore"/>
/// plus the <c>hosting</c> mode that is always agent-owned.
/// </summary>
/// <remarks>
/// <para>
/// Both the manifest-apply path and the dedicated
/// <c>PUT /api/v1/agents/{id}/execution</c> HTTP surface write through
/// this interface so the two paths cannot drift on shape or validation.
/// Partial updates are supported: a non-null field replaces the
/// corresponding slot; a null field leaves the existing persisted value
/// alone.
/// </para>
/// <para>
/// <c>hosting</c> is agent-exclusive — a unit cannot change whether an
/// agent is ephemeral or persistent. The other fields (image, agent,
/// provider, model) participate in the agent → unit →
/// fail resolution chain documented in
/// <c>docs/architecture/units.md</c>.
/// </para>
/// </remarks>
public interface IAgentExecutionStore
{
    /// <summary>
    /// Returns the agent's persisted execution shape, or <c>null</c> when
    /// no block has been declared. Reads the raw on-disk block; the
    /// inheritance merge with unit defaults happens in the
    /// <see cref="IAgentDefinitionProvider"/>.
    /// </summary>
    Task<AgentExecutionShape?> GetAsync(
        string agentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the agent's execution block in place. Partial update
    /// semantics — non-null fields replace the existing slot; null
    /// fields leave the persisted value alone. Implementations must
    /// preserve every other property on the Definition document.
    /// </summary>
    Task SetAsync(
        string agentId,
        AgentExecutionShape shape,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Strips the entire execution block from the agent's persisted
    /// definition. Idempotent.
    /// </summary>
    Task ClearAsync(
        string agentId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// On-disk shape of an agent's persisted <c>execution:</c> block. Each
/// field is independently nullable — a partial update sends only the
/// fields the caller wants to change.
/// </summary>
/// <remarks>
/// #1732: the standalone <c>Tool</c> slot was dropped — the execution
/// tool is derived 1:1 from <see cref="Agent"/> (the runtime registry
/// id) via the catalogue runtime's
/// <see cref="Cvoya.Spring.Core.Catalog.AgentRuntime.Launcher"/> field.
/// ADR-0039 G8 removes the container-runtime selector from this record;
/// the host process owns that platform setting.
/// </remarks>
/// <param name="Image">Container image reference.</param>
/// <param name="Provider">LLM model provider (Spring Voyage Agent–specific).</param>
/// <param name="Model">Model identifier (Spring Voyage Agent–specific).</param>
/// <param name="Hosting">Hosting mode (ephemeral / persistent). Agent-exclusive.</param>
/// <param name="Agent">Agent-runtime registry id (e.g. <c>claude</c>, <c>codex</c>, <c>spring-voyage</c>). Determines both the validation pipeline and the launcher selected at dispatch (via the catalogue runtime's <c>Launcher</c> field).</param>
public record AgentExecutionShape(
    string? Image = null,
    string? Provider = null,
    string? Model = null,
    string? Hosting = null,
    string? Agent = null)
{
    /// <summary>True when every field is null / whitespace.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Image)
        && string.IsNullOrWhiteSpace(Provider)
        && string.IsNullOrWhiteSpace(Model)
        && string.IsNullOrWhiteSpace(Hosting)
        && string.IsNullOrWhiteSpace(Agent);
}
