// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Execution;

/// <summary>
/// Persists one row of cross-process runtime state for a persistent agent
/// deployment. The row is the source of truth that lets the API and worker
/// host processes share a single view of "this agent is up, here is its
/// endpoint / container / health" — without this row each
/// <see cref="PersistentAgentRegistry"/> singleton sees only the writes its
/// own process produced, and the API's "Persistent deployment" badge reads
/// <c>Not deployed</c> for any agent the worker auto-deployed via inbound
/// message (#2468).
///
/// <para>
/// 1:1 with <see cref="AgentDefinitionEntity"/>: keyed by the agent's
/// stable Guid, presence of the row means "registry tracks this agent."
/// The companion <c>persistent_agent_runtime</c> table is intentionally
/// separate from <c>agent_live_config</c> because the lifetime is
/// different — <c>agent_live_config</c> persists across deploys / restarts;
/// this row is created on
/// <see cref="PersistentAgentRegistry.Register"/> and dropped on
/// <see cref="PersistentAgentRegistry.UndeployAsync"/> or
/// <see cref="PersistentAgentRegistry.StopContainerAsync"/>.
/// </para>
///
/// <para>
/// Per the issue plan, the row mirrors the public fields of the in-memory
/// <see cref="PersistentAgentEntry"/> verbatim. The agent
/// <see cref="Core.Execution.AgentDefinition"/> needed for restart is
/// <em>not</em> persisted — the restart path on a cache miss in another
/// process rehydrates the definition via <c>IAgentDefinitionProvider</c>.
/// </para>
/// </summary>
public class PersistentAgentRuntimeEntity : ITenantScopedEntity
{
    /// <summary>
    /// Stable Guid identity of the agent the row describes. Primary key
    /// and FK back to <see cref="AgentDefinitionEntity.Id"/> (1:1).
    /// </summary>
    public Guid AgentId { get; set; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>
    /// The A2A endpoint URL the running agent service is reachable at.
    /// Stored as the canonical absolute string form (<c>http://host:port/</c>).
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// The container identifier of the agent's backing container, or
    /// <c>null</c> for legacy externally-registered persistent agents
    /// that have no container we manage.
    /// </summary>
    public string? ContainerId { get; set; }

    /// <summary>
    /// UTC timestamp when the agent service was started.
    /// </summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>
    /// Current health status of the agent. Persisted as the
    /// <see cref="AgentHealthStatus"/> ordinal.
    /// </summary>
    public AgentHealthStatus HealthStatus { get; set; } = AgentHealthStatus.Healthy;

    /// <summary>
    /// Number of consecutive health-check failures observed for this
    /// agent. Reset to <c>0</c> on the first successful probe after a
    /// failure run.
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// Sidecar container id (Dapr-sidecar agents only); <c>null</c> for
    /// agents launched without a sidecar.
    /// </summary>
    public string? SidecarId { get; set; }

    /// <summary>
    /// Name of the per-deployment network created for the agent +
    /// sidecar pair; <c>null</c> when no sidecar network was created.
    /// </summary>
    public string? SidecarNetworkName { get; set; }

    /// <summary>
    /// The container image the agent is running (when known). Stored so
    /// readers in a different process can render the deployment badge's
    /// image field without having to round-trip through
    /// <c>IAgentDefinitionProvider</c>.
    /// </summary>
    public string? Image { get; set; }

    /// <summary>
    /// Process identifier of the host that registered the row, in the
    /// form <c>"{machine}/{processName}/{pid}"</c>. Diagnostics only —
    /// surfaces which host actually launched the container, so an
    /// operator looking at <c>Not deployed</c> against a live container
    /// can immediately see "process A registered it" vs "process B."
    /// Not used for any routing or correctness decision.
    /// </summary>
    public string? OwnerHost { get; set; }

    /// <summary>
    /// UTC timestamp of the last write to this row. Stamped by the
    /// <c>SpringDbContext</c> audit-timestamp pipeline.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
