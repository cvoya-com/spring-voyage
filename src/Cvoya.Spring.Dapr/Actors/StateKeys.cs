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
    /// State key for the currently active conversation channel.
    /// </summary>
    public const string ActiveConversation = "Agent:ActiveConversation";

    /// <summary>
    /// State key for the list of pending conversation channels.
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
    /// State key prefix for agent checkpoints, suffixed with the conversation ID.
    /// Full key format: <c>Agent:Checkpoint:{ConversationId}</c>.
    /// </summary>
    public const string CheckpointPrefix = "Agent:Checkpoint:";

    /// <summary>
    /// State key for the agent's initiative state.
    /// </summary>
    public const string InitiativeState = "Agent:InitiativeState";

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
    /// State key for the clone identity record, stored on clone agents.
    /// </summary>
    public const string CloneIdentity = "Agent:CloneIdentity";

    /// <summary>
    /// State key for the list of child clone IDs, stored on parent agents.
    /// </summary>
    public const string CloneChildren = "Agent:CloneChildren";
}