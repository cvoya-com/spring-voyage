// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

/// <summary>
/// Centralized constants for Dapr actor state keys.
/// Prevents typos and ensures consistency across parallel work.
/// </summary>
public static class StateKeys
{
    /// <summary>
    /// State key for the currently active thread channel.
    /// </summary>
    public const string ActiveThread = "Agent:ActiveThread";

    /// <summary>
    /// State key for the list of pending thread channels.
    /// </summary>
    public const string PendingConversations = "Agent:PendingConversations";

    /// <summary>
    /// State key for the observation channel (batched events).
    /// </summary>
    public const string ObservationChannel = "Agent:ObservationChannel";

    // ADR-0040 / #2048: AgentDefinition ("Agent:Definition") was dropped.
    // Cloning re-reads the parent's row from agent_definitions in EF
    // directly; there is no actor-state copy of the YAML template.

    /// <summary>
    /// State key prefix for agent checkpoints, suffixed with the thread ID.
    /// Full key format: <c>Agent:Checkpoint:{ThreadId}</c>.
    /// </summary>
    public const string CheckpointPrefix = "Agent:Checkpoint:";

    /// <summary>
    /// State key for the agent's initiative state.
    /// </summary>
    public const string InitiativeState = "Agent:InitiativeState";

    /// <summary>
    /// State key indicating whether the initiative reminder has been registered for this agent.
    /// </summary>
    public const string InitiativeReminderRegistered = "Agent:InitiativeReminderRegistered";

    /// <summary>
    /// State key for unit members.
    /// </summary>
    public const string Members = "Unit:Members";

    // ADR-0040 / #2049: Unit:Policies actor-state copy was dropped.
    // UnitPolicyEntity (unit_policies EF table) is the single write
    // path for unit policies; the PolicyUpdate control message is now
    // an audit-only notification.

    /// <summary>
    /// State key for the unit directory cache.
    /// </summary>
    public const string DirectoryCache = "Unit:DirectoryCache";

    // ADR-0040 / #2049: Unit:Definition was dropped. Cloning re-reads
    // the parent's row from unit_definitions in EF directly; there is
    // no actor-state copy of the YAML template (parallels the
    // Agent:Definition drop in #2048).

    /// <summary>
    /// State key for the connector's connection status.
    /// </summary>
    public const string ConnectorStatus = "Connector:Status";

    /// <summary>
    /// State key for the connector type (e.g., "github", "slack").
    /// </summary>
    public const string ConnectorType = "Connector:Type";

    /// <summary>
    /// State key for the connector configuration.
    /// </summary>
    public const string ConnectorConfig = "Connector:Config";

    // ADR-0040: Human:Identity, Human:Permission, and
    // Human:NotificationPreferences moved to columns on the humans EF table.
    // The HumanActor reads them from SpringDbContext on every call; no
    // actor-state copy is maintained.

    // #2044 / ADR-0040: HumanUnitPermissions ("Human:UnitPermissions") and
    // HumanPermissions ("Unit:HumanPermissions") were dropped. Unit ACL
    // grants live in the unit_human_permissions EF table; the
    // PermissionService and UnitActor read/write that table directly.

    /// <summary>
    /// State key for the clone identity record, stored on clone agents.
    /// </summary>
    public const string CloneIdentity = "Agent:CloneIdentity";

    /// <summary>
    /// State key for the list of child clone IDs, stored on parent agents.
    /// </summary>
    public const string CloneChildren = "Agent:CloneChildren";

    /// <summary>
    /// State key for the last stream event sequence number processed by the agent.
    /// </summary>
    public const string StreamSequence = "Agent:StreamSequence";

    /// <summary>
    /// State key for the agent's streaming configuration (enabled, topic, etc.).
    /// </summary>
    public const string StreamConfig = "Agent:StreamConfig";

    // ADR-0040 / #2048: AgentCostTotal ("Agent:CostTotal") was dropped.
    // The accumulated agent cost is computed at read time as
    // SUM(cost) FROM cost_records; the cost endpoints query
    // CostRecord aggregations directly, so the actor-state running
    // total had no live consumers and was a known-stale duplicate.

    // ADR-0040 / #2048: the agent live-config keys (Agent:Model,
    // Agent:Specialty, Agent:Enabled, Agent:ExecutionMode, Agent:Skills,
    // Agent:Expertise) and the legacy parent-unit pointer
    // (Agent:ParentUnit) were moved to EF. The first four live on the
    // agent_live_config row; skills and expertise live on
    // agent_skill_grants and agent_expertise rows; ParentUnit is
    // derived from unit_memberships and no longer mirrored on the
    // agent. AgentActor reads / writes via IAgentStateCoordinator,
    // which routes through IAgentLiveConfigStore.

    // ADR-0040 / #2045: the per-agent, per-unit, and tenant-level cost-
    // budget keys (`Agent:CostBudget`, `Unit:CostBudget`,
    // `Tenant:CostBudget`) were removed. Cost budgets now live in the
    // tenant-scoped `budget_limits` EF table and are read / written via
    // `BudgetEndpoints` and `BudgetEnforcer`.

    /// <summary>
    /// State key for the unit's lifecycle status.
    /// </summary>
    public const string UnitStatus = "Unit:Status";

    // ADR-0040 / #2049: the unit live-config keys (Unit:Model,
    // Unit:Color, Unit:Provider, Unit:Hosting, Unit:Boundary,
    // Unit:PermissionInheritance, Unit:OwnExpertise) were moved to EF.
    // Model / Color / Provider / Hosting live on the unit_live_config
    // row alongside Boundary (jsonb) and PermissionInheritance (int);
    // own-expertise lives on the unit_expertise rows. UnitActor reads
    // / writes via IUnitStateCoordinator, which routes through
    // IUnitLiveConfigStore.

    // ADR-0040 / #2050: Unit:ConnectorBinding and Unit:ConnectorMetadata
    // were moved to the unit_connector_bindings EF table. Connector
    // bindings now live on a single relational row keyed by
    // (tenant, unit) so the binding survives actor restarts and the
    // connector lifecycle hooks can resolve from a single SQL read.
    // The connector-owned runtime metadata column (e.g. GitHub webhook
    // ids) lives on the same row. Connector:Status, Connector:Type,
    // and Connector:Config remain instance-local actor state on
    // ConnectorActor per ADR-0040.

    /// <summary>
    /// State key for the agent's pending mid-flight amendments queue
    /// (<see cref="Core.Messaging.PendingAmendment"/>). The dispatcher drains
    /// the list between tool calls and at model-call boundaries; entries
    /// live here until consumed. See #142.
    /// </summary>
    public const string AgentPendingAmendments = "Agent:PendingAmendments";

    /// <summary>
    /// State key for the agent's "paused awaiting clarification" flag — set
    /// when a <c>StopAndWait</c>-priority amendment is accepted and cleared
    /// when the agent is explicitly resumed. See #142.
    /// </summary>
    public const string AgentPaused = "Agent:Paused";

    // ADR-0040 / #2048: Agent:Expertise was moved to the
    // agent_expertise EF table — see the comment block above.

    // ADR-0040 / #2049: Unit:OwnExpertise, Unit:Boundary, and
    // Unit:PermissionInheritance were moved to EF — see the
    // unit_live_config / unit_expertise comment block above.

    // ADR-0040 / #2051: Agent:CloningPolicy and Tenant:CloningPolicy were
    // moved to the cloning_policies EF table. Agent and tenant-wide
    // cloning policies now live on a single tenant-scoped relational
    // table keyed by (tenant_id, scope_type, scope_id) so the policy
    // payload survives actor restarts and so resolution becomes a single
    // SQL read in the enforcer. See EfAgentCloningPolicyRepository.

    // #1732: The pre-existing UnitTool key (`Unit:Tool`) was removed —
    // the execution tool is now derived from the runtime registry via the
    // unit's execution.agent slot, so a duplicate actor-state copy is
    // unnecessary. Persisted state keyed on `Unit:Tool` from before
    // #1732 is silently ignored.

    /// <summary>
    /// State key for the <see cref="ContainerSupervisorActor"/>'s persisted
    /// supervision state (container id, hosting mode, restart count, etc.).
    /// Stored on the <c>ContainerSupervisorActor</c> (keyed by agent id).
    /// D3d — ADR-0029 § "Failure recovery".
    /// </summary>
    public const string SupervisorState = "Supervisor:State";

    /// <summary>
    /// State key for the human actor's per-thread read cursor map
    /// (<c>Dictionary&lt;string, DateTimeOffset&gt;</c> mapping
    /// <c>threadId → lastReadAt</c>). Absent entries mean the thread
    /// has never been read; the inbox unread-count computation treats
    /// absent entries as <see cref="DateTimeOffset.MinValue"/> so all
    /// events count as unread. Written by the
    /// <c>POST /api/v1/inbox/{threadId}/mark-read</c> endpoint (#1477).
    /// </summary>
    public const string HumanLastReadAt = "Human:LastReadAt";
}
