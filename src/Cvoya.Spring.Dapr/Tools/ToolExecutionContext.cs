/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Dapr.Tools;

using Cvoya.Spring.Core.Messaging;
using global::Dapr.Actors.Runtime;

/// <summary>
/// Provides execution context for platform tools running within a Dapr actor.
/// Contains the agent's address, current conversation ID, and access to actor state.
/// </summary>
/// <param name="AgentAddress">The address of the agent executing the tool.</param>
/// <param name="ConversationId">The active conversation identifier, or <c>null</c> if none.</param>
/// <param name="StateManager">The Dapr actor state manager for reading and writing state.</param>
public record ToolExecutionContext(
    Address AgentAddress,
    string? ConversationId,
    IActorStateManager StateManager);

/// <summary>
/// Provides ambient access to the current <see cref="ToolExecutionContext"/>.
/// Set by the <see cref="ToolDispatcher"/> before each tool invocation.
/// </summary>
public class ToolExecutionContextAccessor
{
    /// <summary>
    /// Gets or sets the current tool execution context.
    /// </summary>
    public ToolExecutionContext? Current { get; set; }
}
