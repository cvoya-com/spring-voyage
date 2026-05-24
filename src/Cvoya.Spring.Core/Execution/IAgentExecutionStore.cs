// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Read/write seam for the agent's persisted <c>execution:</c> block on
/// the <c>AgentDefinitions.Definition</c> JSON (#601 / #603 / #409
/// B-wide). Exposes the same <c>(runtime, model, image)</c> shape as <see cref="IUnitExecutionStore"/>
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
/// agent is ephemeral or persistent. The other fields (image, runtime,
/// model) participate in the agent → unit →
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
/// On-disk shape of an agent's persisted <c>execution:</c> block. Per the
/// ADR-0038 amendment (#2634) the block carries exactly
/// <c>(runtime, model{provider, id}, image, hosting)</c>. Each field is
/// independently nullable — a partial update sends only the fields the
/// caller wants to change.
/// </summary>
/// <remarks>
/// ADR-0038: the execution tool is derived 1:1 from <see cref="Runtime"/>
/// (the runtime registry id) via the catalogue runtime's
/// <see cref="Cvoya.Spring.Core.Catalog.AgentRuntime.Launcher"/> field.
/// ADR-0039 G8 removes the container-runtime selector from this record;
/// the host process owns that platform setting.
/// </remarks>
/// <param name="Image">Container image reference.</param>
/// <param name="Model">Structured <c>{provider, id}</c> model selector.</param>
/// <param name="Hosting">Hosting mode (ephemeral / persistent). Agent-exclusive.</param>
/// <param name="Runtime">Agent-runtime registry id (e.g. <c>claude-code</c>, <c>codex</c>, <c>spring-voyage</c>). Determines both the validation pipeline and the launcher selected at dispatch (via the catalogue runtime's <c>Launcher</c> field).</param>
/// <param name="SystemPromptMode">
/// How the platform-assembled system prompt combines with the runtime's own
/// default system prompt (#2691 / #2667). Wire form is the lower-case
/// enum literal (<c>"append"</c> / <c>"replace"</c>); <c>null</c> means
/// "inherit from the parent unit's default".
/// </param>
public record AgentExecutionShape(
    string? Image = null,
    Cvoya.Spring.Core.Catalog.Model? Model = null,
    string? Hosting = null,
    string? Runtime = null,
    string? SystemPromptMode = null)
{
    /// <summary>True when every field is null / whitespace.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Image)
        && Model is null
        && string.IsNullOrWhiteSpace(Hosting)
        && string.IsNullOrWhiteSpace(Runtime)
        && string.IsNullOrWhiteSpace(SystemPromptMode);
}
