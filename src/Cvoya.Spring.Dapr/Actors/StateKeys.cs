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
    /// State key prefix for a per-thread channel (#2076 / ADR-0030 §3 §44).
    /// Full key format: <c>Agent:Channel:{ThreadId}</c>. Each entry holds
    /// the per-thread <see cref="Core.Messaging.ThreadChannel"/> with its
    /// queued messages and a <c>Dispatching</c> flag that prevents a fresh
    /// inbound on the same thread from launching a parallel dispatcher
    /// while the channel is mid-drain. The set of currently-known
    /// thread ids is tracked separately on <see cref="ChannelIndex"/>
    /// because the Dapr actor state manager has no key-enumeration
    /// primitive.
    /// </summary>
    public const string ChannelPrefix = "Agent:Channel:";

    /// <summary>
    /// State key for the index of thread ids that currently have an
    /// associated <see cref="ChannelPrefix"/> entry. Stored as
    /// <c>List&lt;string&gt;</c>. Used so the agent can enumerate its
    /// per-thread channels for status queries and clone state-copy
    /// activities without relying on a key-prefix scan (which the Dapr
    /// actor state manager does not expose). The index is updated
    /// transactionally with the channel writes; a thread id is removed
    /// when its channel is removed.
    /// </summary>
    public const string ChannelIndex = "Agent:ChannelIndex";

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

    // ADR-0040 / #2052: Unit:Members was dropped. The unit member graph
    // (agent edges in unit_memberships + sub-unit edges in
    // unit_subunit_memberships) is the single source of truth — UnitActor
    // reads / writes through IUnitMemberGraphStore on every call.
    // Persisted state keyed on `Unit:Members` from before #2052 is
    // silently ignored (no migration needed; the actor never reads from
    // state for the member graph after this PR).

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

    /// <summary>
    /// State key (boolean) marking a unit as awaiting an automatic transition
    /// to <c>Running</c> as soon as validation succeeds (#2156). Set by the
    /// creation / package-install path on every unit that lands in
    /// <c>Validating</c> at create time; consumed and cleared by
    /// <see cref="UnitActor.CompleteValidationAsync"/> when the workflow
    /// reports success. Absent / <c>false</c> means "leave the unit in
    /// Stopped after validation" — the original behaviour preserved for the
    /// manual <c>/revalidate</c> path.
    /// </summary>
    public const string PendingAutoStart = "Unit:PendingAutoStart";

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

    /// <summary>
    /// State key for an agent's installation-lifecycle status (#2156).
    /// Stored as a <see cref="Cvoya.Spring.Core.Agents.AgentLifecycleStatus"/>
    /// enum value (Active / Error). Set by
    /// <c>DefaultPackageArtefactActivator</c> at install time. Absent
    /// means the agent activated successfully through the pre-#2156
    /// path — readers default to <c>Active</c>.
    /// </summary>
    public const string AgentLifecycleStatus = "Agent:LifecycleStatus";

    /// <summary>
    /// State key for the diagnostic message accompanying an
    /// <see cref="Cvoya.Spring.Core.Agents.AgentLifecycleStatus.Error"/>
    /// row (#2156). Cleared whenever the lifecycle status flips back to
    /// <see cref="Cvoya.Spring.Core.Agents.AgentLifecycleStatus.Active"/>.
    /// </summary>
    public const string AgentLifecycleError = "Agent:LifecycleError";
}
