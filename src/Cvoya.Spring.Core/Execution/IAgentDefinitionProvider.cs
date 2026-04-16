// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Resolves an agent identifier to the concrete configuration needed to launch
/// its external runtime (image, tool, instructions). The OSS default reads from
/// the platform's agent-definition store; the private cloud repo may override to
/// add tenant scoping, caching, or alternative storage.
/// </summary>
public interface IAgentDefinitionProvider
{
    /// <summary>
    /// Gets the definition for the given agent id, or <c>null</c> when no agent
    /// matches. Implementations must not throw for missing agents.
    /// </summary>
    /// <param name="agentId">The agent identifier (the actor id / YAML <c>agent.id</c>).</param>
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
public record AgentDefinition(
    string AgentId,
    string Name,
    string? Instructions,
    AgentExecutionConfig? Execution);

/// <summary>
/// Determines how an agent process is hosted across dispatch invocations.
/// </summary>
public enum AgentHostingMode
{
    /// <summary>
    /// A fresh container is started per dispatch, does its work, and is cleaned up.
    /// This is the default and matches the existing behaviour.
    /// </summary>
    Ephemeral,

    /// <summary>
    /// A long-lived service receives messages over its lifetime. The platform
    /// starts it on first dispatch and keeps it running.
    /// </summary>
    Persistent
}

/// <summary>
/// Execution configuration derived from the agent YAML <c>execution:</c> block
/// (or the legacy <c>ai.environment</c> block). The two fields a launcher
/// fundamentally needs are the external <paramref name="Tool"/> and the
/// container <paramref name="Image"/>.
/// </summary>
/// <param name="Tool">The external agent tool identifier (e.g. <c>claude-code</c>, <c>codex</c>).</param>
/// <param name="Image">
/// The container image to run. Nullable for A2A-native agents that do not
/// require a container image (e.g. agents running as standalone services).
/// </param>
/// <param name="Runtime">Optional container runtime hint (e.g. <c>docker</c>, <c>podman</c>).</param>
/// <param name="Hosting">
/// The hosting mode for the agent. Defaults to <see cref="AgentHostingMode.Ephemeral"/>.
/// </param>
public record AgentExecutionConfig(
    string Tool,
    string? Image,
    string? Runtime = null,
    AgentHostingMode Hosting = AgentHostingMode.Ephemeral);