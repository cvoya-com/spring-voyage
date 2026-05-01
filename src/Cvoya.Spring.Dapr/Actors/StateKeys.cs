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
    public const string ActiveConversation = "Agent:ActiveConversation";

    /// <summary>
    /// State key for the list of pending thread channels.
    /// </summary>
    public const string PendingConversations = "Agent:PendingConversations";

    /// <summary>
    /// State key for the observation channel (batched events).
    /// </summary>
    public const string ObservationChannel = "Agent:ObservationChannel";

    /// <summary>
    /// State key for the agent definition.
    /// </summary>
    public const string AgentDefinition = "Agent:Definition";

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

    /// <summary>
    /// State key for unit policies.
    /// </summary>
    public const string Policies = "Unit:Policies";

    /// <summary>
    /// State key for the unit directory cache.
    /// </summary>
    public const string DirectoryCache = "Unit:DirectoryCache";

    /// <summary>
    /// State key for the unit definition.
    /// </summary>
    public const string UnitDefinition = "Unit:Definition";

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

    /// <summary>
    /// State key for the human actor's identity.
    /// </summary>
    public const string HumanIdentity = "Human:Identity";

    /// <summary>
    /// State key for the human actor's permission level.
    /// </summary>
    public const string HumanPermission = "Human:Permission";

    /// <summary>
    /// State key for the human actor's notification preferences.
    /// </summary>
    public const string HumanNotificationPreferences = "Human:NotificationPreferences";

    /// <summary>
    /// State key for the human actor's unit-scoped permission map (unitId to PermissionLevel).
    /// </summary>
    public const string HumanUnitPermissions = "Human:UnitPermissions";

    /// <summary>
    /// State key for the unit actor's human permission entries (humanId to UnitPermissionEntry).
    /// </summary>
    public const string HumanPermissions = "Unit:HumanPermissions";

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

    /// <summary>
    /// State key for the agent's accumulated cost total.
    /// </summary>
    public const string AgentCostTotal = "Agent:CostTotal";

    /// <summary>
    /// State key for the agent's preferred LLM model identifier (<c>AgentMetadata.Model</c>).
    /// </summary>
    public const string AgentModel = "Agent:Model";

    /// <summary>
    /// State key for the agent's specialty label (<c>AgentMetadata.Specialty</c>).
    /// </summary>
    public const string AgentSpecialty = "Agent:Specialty";

    /// <summary>
    /// State key for the agent's enabled flag (<c>AgentMetadata.Enabled</c>).
    /// Unset defaults to <c>true</c>; explicit <c>false</c> causes
    /// orchestration strategies to skip the agent.
    /// </summary>
    public const string AgentEnabled = "Agent:Enabled";

    /// <summary>
    /// State key for the agent's execution mode (<c>AgentMetadata.ExecutionMode</c>).
    /// </summary>
    public const string AgentExecutionMode = "Agent:ExecutionMode";

    /// <summary>
    /// State key for the agent's parent-unit pointer (<c>AgentMetadata.ParentUnit</c>).
    /// Maintained by the unit's assign / unassign endpoints alongside the
    /// unit's <see cref="Members"/> list.
    /// </summary>
    public const string AgentParentUnit = "Agent:ParentUnit";

    /// <summary>
    /// State key for the agent's configured skill list (tool names the agent
    /// is allowed to invoke). Stored as <c>List&lt;string&gt;</c>. Replaced
    /// in full by <c>SetSkillsAsync</c>; no partial-merge semantics because
    /// an empty list is a legitimate "disable everything" state and should
    /// not be indistinguishable from "leave alone."
    /// </summary>
    public const string AgentSkills = "Agent:Skills";

    /// <summary>
    /// State key for the agent's cost budget limit.
    /// </summary>
    public const string AgentCostBudget = "Agent:CostBudget";

    /// <summary>
    /// State key for the tenant-level cost budget limit.
    /// </summary>
    public const string TenantCostBudget = "Tenant:CostBudget";

    /// <summary>
    /// State key for the unit-level cost budget limit. Mirrors
    /// <see cref="AgentCostBudget"/> but scoped to a unit so that
    /// `spring cost set-budget --scope unit` and the portal's per-unit
    /// "Edit budget" action can both target the same key.
    /// </summary>
    public const string UnitCostBudget = "Unit:CostBudget";

    /// <summary>
    /// State key for the unit's lifecycle status.
    /// </summary>
    public const string UnitStatus = "Unit:Status";

    /// <summary>
    /// State key for the unit's model hint (e.g., default LLM identifier).
    /// Surfaced through <see cref="Core.Units.UnitMetadata"/>.
    /// </summary>
    public const string UnitModel = "Unit:Model";

    /// <summary>
    /// State key for the unit's UI color hint.
    /// Surfaced through <see cref="Core.Units.UnitMetadata"/>.
    /// </summary>
    public const string UnitColor = "Unit:Color";

    /// <summary>
    /// State key for the unit's generic connector binding
    /// (<see cref="Connectors.UnitConnectorBinding"/>): a
    /// <c>(TypeId, JsonElement)</c> pair that identifies which connector
    /// owns the unit and carries the connector-specific typed config.
    /// Present while the unit is bound; absent otherwise.
    /// </summary>
    public const string UnitConnectorBinding = "Unit:ConnectorBinding";

    /// <summary>
    /// State key for connector-owned runtime metadata persisted on a unit —
    /// e.g. the GitHub webhook id created at /start and needed by /stop so
    /// it can call <c>DELETE /repos/{owner}/{repo}/hooks/{id}</c>. Stored as
    /// an opaque <see cref="System.Text.Json.JsonElement"/> because the
    /// actor has no knowledge of any individual connector's shape.
    /// </summary>
    public const string UnitConnectorMetadata = "Unit:ConnectorMetadata";

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

    /// <summary>
    /// State key for the agent's configured expertise domains. Stored as a
    /// <c>List&lt;ExpertiseDomain&gt;</c>. Replaced in full by
    /// <c>SetExpertiseAsync</c>; empty list is a legitimate "no expertise"
    /// state. See #412.
    /// </summary>
    public const string AgentExpertise = "Agent:Expertise";

    /// <summary>
    /// State key for the unit's own (non-aggregated) expertise domains —
    /// the capabilities the unit advertises in its own right, independent
    /// of its members. Stored as a <c>List&lt;ExpertiseDomain&gt;</c>.
    /// Recursive composition over the member graph happens at read time in
    /// <see cref="Cvoya.Spring.Core.Capabilities.IExpertiseAggregator"/>;
    /// this state key only holds the slice <em>owned by the unit</em>. See #412.
    /// </summary>
    public const string UnitOwnExpertise = "Unit:OwnExpertise";

    /// <summary>
    /// State key for the unit's boundary configuration — the
    /// <see cref="Cvoya.Spring.Core.Capabilities.UnitBoundary"/> record that
    /// controls which aggregated expertise entries are opaque, projected, or
    /// synthesised when an outside caller reads the unit's effective
    /// expertise. Empty / never-set means "transparent" (no boundary rules).
    /// See #413.
    /// </summary>
    public const string UnitBoundary = "Unit:Boundary";

    /// <summary>
    /// State key for the unit's permission-inheritance mode
    /// (<see cref="Cvoya.Spring.Dapr.Auth.UnitPermissionInheritance"/>). Unset
    /// defaults to <see cref="Cvoya.Spring.Dapr.Auth.UnitPermissionInheritance.Inherit"/>
    /// — ancestor permission grants cascade down to the unit. Setting this
    /// key to <see cref="Cvoya.Spring.Dapr.Auth.UnitPermissionInheritance.Isolated"/>
    /// causes the hierarchy-aware permission resolver to stop walking up
    /// through this unit, so ancestor grants do not apply. This is the
    /// permission-layer analogue of the boundary's opacity rules (#413) and
    /// is read by <c>PermissionService.ResolveEffectivePermissionAsync</c>
    /// (#414).
    /// </summary>
    public const string UnitPermissionInheritance = "Unit:PermissionInheritance";

    /// <summary>
    /// State-key prefix for the persistent cloning policy recorded against
    /// a specific agent — the
    /// <see cref="Cvoya.Spring.Core.Cloning.AgentCloningPolicy"/> value
    /// <c>IAgentCloningPolicyEnforcer</c> evaluates on every clone request.
    /// Full key format: <c>Agent:CloningPolicy:{agentId}</c>. Empty /
    /// never-set means "no agent-scoped constraint" — the enforcer falls
    /// through to <see cref="TenantCloningPolicy"/>. See #416.
    /// </summary>
    public const string AgentCloningPolicy = "Agent:CloningPolicy";

    /// <summary>
    /// State-key prefix for the tenant-wide persistent cloning policy —
    /// the default <see cref="Cvoya.Spring.Core.Cloning.AgentCloningPolicy"/>
    /// applied to every agent that has no agent-scoped policy. Full key
    /// format: <c>Tenant:CloningPolicy:{tenantId}</c>. Numeric caps
    /// collapse to the tightest non-null value across scopes so a tenant
    /// ceiling cannot be relaxed by an agent-scoped override. See #416.
    /// </summary>
    public const string TenantCloningPolicy = "Tenant:CloningPolicy";

    /// <summary>
    /// State key for the unit's external agent-tool identifier
    /// (e.g. <c>claude-code</c>, <c>dapr-agent</c>). Surfaced through
    /// <see cref="Core.Units.UnitMetadata"/> so the unit-detail GET
    /// returns the value the operator passed at create time. Distinct
    /// from the unit's <c>execution:</c> block (which the scheduler
    /// consults) — both are kept in sync by <c>UnitCreationService</c>.
    /// See #1065.
    /// </summary>
    public const string UnitTool = "Unit:Tool";

    /// <summary>
    /// State key for the unit's LLM provider identifier
    /// (e.g. <c>ollama</c>, <c>openai</c>). Surfaced through
    /// <see cref="Core.Units.UnitMetadata"/>. Distinct from the
    /// container runtime (<c>docker | podman</c>) which lives on the
    /// unit's <c>execution:</c> block as <c>runtime</c>. See #1065.
    /// </summary>
    public const string UnitProvider = "Unit:Provider";

    /// <summary>
    /// State key for the <see cref="ContainerSupervisorActor"/>'s persisted
    /// supervision state (container id, hosting mode, restart count, etc.).
    /// Stored on the <c>ContainerSupervisorActor</c> (keyed by agent id).
    /// D3d — ADR-0029 § "Failure recovery".
    /// </summary>
    public const string SupervisorState = "Supervisor:State";

    /// <summary>
    /// State key for the unit's hosting hint
    /// (e.g. <c>ephemeral</c>, <c>persistent</c>). Surfaced through
    /// <see cref="Core.Units.UnitMetadata"/>. See #1065.
    /// </summary>
    public const string UnitHosting = "Unit:Hosting";

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