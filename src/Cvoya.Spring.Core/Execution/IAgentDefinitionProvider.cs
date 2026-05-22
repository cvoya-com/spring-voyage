// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using Cvoya.Spring.Core.Catalog;

/// <summary>
/// Resolves an agent or unit identifier to the concrete configuration needed to
/// launch its external runtime (image, tool, instructions). The OSS default
/// reads from the platform's agent-definition store and, for unit-shaped
/// subjects, falls back to the unit-definition store; the private cloud repo may
/// override to add tenant scoping, caching, or alternative storage.
/// </summary>
public interface IAgentDefinitionProvider
{
    /// <summary>
    /// Gets the definition for the given agent or unit id, or <c>null</c> when
    /// no addressable runtime subject matches. Implementations must not throw
    /// for missing subjects.
    /// </summary>
    /// <param name="agentId">The subject identifier (the actor id in canonical Guid wire form).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<AgentDefinition?> GetByIdAsync(string agentId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Normalised view of an agent definition as consumed by the execution layer.
/// </summary>
/// <param name="AgentId">The agent identifier.</param>
/// <param name="Name">Human-readable display name.</param>
/// <param name="Instructions">The agent-specific instructions (prompt Layer 4). May be null when absent.</param>
/// <param name="Execution">Execution/runtime configuration. Required for delegated execution.</param>
/// <param name="UnitId">
/// Canonical Guid wire form (32-char lowercase no-dash hex per
/// <see cref="Cvoya.Spring.Core.Identifiers.GuidFormatter.Format"/>) of the
/// agent's owning unit. For a non-unit agent this is the first parent unit
/// reported by <c>IUnitMembershipRepository.ListByAgentAsync</c> (the same
/// "primary unit" rule used by <c>AgentMetadata.ParentUnit</c>). For a
/// unit-as-agent (ADR-0039) this is the unit's own id — the unit is its own
/// owning scope. <c>null</c> when the agent has no membership yet, which the
/// dispatcher / credential resolver treats as "no unit-scoped credential
/// available" and falls back to tenant-scope.
/// </param>
public record AgentDefinition(
    string AgentId,
    string Name,
    string? Instructions,
    AgentExecutionConfig? Execution,
    string? UnitId = null);

/// <summary>
/// Determines how an agent process is hosted across dispatch invocations.
/// </summary>
public enum AgentHostingMode
{
    /// <summary>
    /// A fresh container is started per dispatch, does its work, and is cleaned up.
    /// </summary>
    Ephemeral,

    /// <summary>
    /// A long-lived service receives messages over its lifetime. The platform
    /// starts it on first dispatch and keeps it running. This is the default.
    /// </summary>
    Persistent,

    /// <summary>
    /// Reserved for the warm-pool dispatch model in #362. Containers are
    /// pre-started up to a low-water mark and pulled from the pool per
    /// dispatch instead of being created on demand. <b>Not implemented in
    /// this release</b> — the dispatcher rejects this value with
    /// <see cref="NotSupportedException"/>. Reserved on the enum now so
    /// agent YAML written against #362 doesn't break the agent provider's
    /// parser before the implementation lands.
    /// </summary>
    Pooled
}

/// <summary>
/// Execution configuration derived from the agent / unit YAML
/// <c>execution:</c> block. Per the ADR-0038 amendment (#2634) the
/// config carries exactly <c>(runtime, model{provider, id}, image,
/// hosting)</c> — one canonical shape shared with the persisted
/// <c>execution:</c> JSON block. The launcher fundamentally needs the
/// runtime registry id (<paramref name="Runtime"/>) — which the
/// catalogue resolves 1:1 to a launcher strategy via
/// <see cref="Cvoya.Spring.Core.Catalog.AgentRuntime.Launcher"/> — and
/// the container <paramref name="Image"/>.
/// </summary>
/// <param name="Runtime">
/// The agent-runtime registry id sourced from the YAML <c>ai.runtime</c>
/// / persisted <c>execution.runtime</c> slot (e.g. <c>claude-code</c>,
/// <c>codex</c>). The dispatcher resolves this through
/// <see cref="Cvoya.Spring.Core.Catalog.IRuntimeCatalog.GetAgentRuntime"/>
/// to pick the matching launcher and to surface the derived launcher id
/// on read-only response surfaces.
/// </param>
/// <param name="Image">
/// The container image to run. Nullable for A2A-native agents that do not
/// require a container image (e.g. agents running as standalone services).
/// </param>
/// <param name="Hosting">
/// The hosting mode for the agent. Defaults to <see cref="AgentHostingMode.Persistent"/>.
/// </param>
/// <param name="Model">
/// Structured <c>{provider, id}</c> model selector (ADR-0038). The
/// provider is intrinsic to the model — there is no separate flat
/// <c>provider</c> slot. <c>null</c> means "let the launcher choose its
/// default".
/// </param>
/// <param name="ConcurrentThreads">
/// Whether the agent may have multiple <c>on_message</c> invocations in flight
/// concurrently — one per distinct thread. <c>true</c> (default, per F1 Q3 /
/// ADR-0030) means the SDK must be re-entrant; <c>false</c> means the platform
/// serialises all <c>on_message</c> calls for the agent.
/// Delivered to the container via <c>SPRING_CONCURRENT_THREADS</c>
/// (D1 spec § 2.2.1).
/// </param>
public record AgentExecutionConfig(
    string Runtime,
    string? Image,
    AgentHostingMode Hosting = AgentHostingMode.Persistent,
    Cvoya.Spring.Core.Catalog.Model? Model = null,
    bool ConcurrentThreads = true);
