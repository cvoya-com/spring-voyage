// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persists one row of operator-editable runtime config for a single agent.
/// 1:1 with <see cref="AgentDefinitionEntity"/>: the row is keyed by the
/// agent's stable Guid and holds the live mutable values that used to be
/// kept in actor state under <c>Agent:Model</c>, <c>Agent:Specialty</c>,
/// <c>Agent:Enabled</c>, and <c>Agent:ExecutionMode</c>.
///
/// <para>
/// Implements ADR-0040 / #2048 for the agent live-config slice. EF is the
/// authoritative store; <c>AgentActor</c> reads through this row on every
/// <c>GetMetadataAsync</c> and writes through it on every
/// <c>SetMetadataAsync</c>. There is no actor-state warm cache in v0.1 —
/// <see cref="OnActivateAsync"/> reads from EF, and the activation read is
/// instrumented with a timing metric so the v0.2 cache decision is data-
/// driven (ADR-0040 § 3).
/// </para>
///
/// <para>
/// <c>ParentUnit</c> is intentionally absent from this entity. Per
/// ADR-0040 the parent-unit pointer lives on the <c>unit_memberships</c>
/// row; the actor's <c>SetMetadataAsync</c> ignores any
/// <see cref="AgentMetadata.ParentUnit"/> field (membership writes go
/// through the assign / unassign endpoints, which already update the
/// membership table directly).
/// </para>
/// </summary>
public class AgentLiveConfigEntity : ITenantScopedEntity
{
    /// <summary>
    /// Stable Guid identity of the agent the row describes. Primary key
    /// and FK back to <see cref="AgentDefinitionEntity.Id"/> (1:1).
    /// </summary>
    public Guid AgentId { get; set; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>
    /// Preferred LLM model identifier for the agent, or <c>null</c> when
    /// the operator has not pinned one and inheritance / defaults apply.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Free-form specialty label (e.g. <c>reviewer</c>,
    /// <c>implementer</c>) surfaced to runtimes and operators for agent
    /// selection; the platform does not route on it. Null when unset.
    /// </summary>
    public string? Specialty { get; set; }

    /// <summary>
    /// Whether the agent processes inbound messages. Defaults to
    /// <c>true</c> — an agent whose row holds an explicit <c>false</c>
    /// skips processing the messages delivered to it.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How the agent participates in dispatch. Persisted as the
    /// <see cref="AgentExecutionMode"/> ordinal so a default of
    /// <see cref="AgentExecutionMode.Auto"/> (0) maps to "unset" without
    /// requiring a nullable column.
    /// </summary>
    public AgentExecutionMode ExecutionMode { get; set; } = AgentExecutionMode.Auto;

    /// <summary>
    /// Sticky flag set the first time the operator (or the YAML seed
    /// path) writes an expertise list for the agent — even an empty
    /// list. Once <c>true</c>, the activation seeder no longer applies
    /// the YAML seed, so runtime edits survive process restart with
    /// "actor-state wins" precedence (ADR-0040 / #488). The flag is the
    /// EF replacement for "is the actor-state expertise key present?"
    /// </summary>
    public bool ExpertiseInitialised { get; set; }

    /// <summary>
    /// Queryable mirror of the agent's lifecycle status (#2981). The
    /// canonical value lives in Dapr actor state under
    /// <c>Agent:LifecycleStatus</c>; <c>AgentActor</c> writes this column in
    /// the same transition turn (via <see cref="Core.Lifecycle.ILifecycleStatusStore"/>)
    /// so the dispatcher cold-start gate, the message-router delivery gate,
    /// and the portal status read-path can consult the status without racing
    /// the non-reentrant actor turn lock. Persisted as the
    /// <see cref="LifecycleStatus"/> ordinal; defaults to
    /// <see cref="LifecycleStatus.Draft"/> (0) for a freshly materialised row
    /// that predates the agent's first transition.
    /// </summary>
    public LifecycleStatus LifecycleStatus { get; set; } = LifecycleStatus.Draft;

    /// <summary>
    /// UTC timestamp of the last write to this row. Stamped by the
    /// <c>SpringDbContext</c> audit-timestamp pipeline.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
