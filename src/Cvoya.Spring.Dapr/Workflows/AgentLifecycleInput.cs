// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows;

using System.Text.Json;

/// <summary>
/// Input for the <see cref="AgentLifecycleWorkflow"/> describing which operation to perform
/// and the agent metadata required.
/// </summary>
/// <param name="Operation">The lifecycle operation to execute.</param>
/// <param name="AgentId">The unique identifier of the agent.</param>
/// <param name="AgentName">The human-readable display name of the agent (used during creation).</param>
/// <param name="Role">An optional role identifier for directory registration (e.g., "backend-engineer").</param>
/// <param name="Definition">An optional JSON payload describing the full agent definition.</param>
public record AgentLifecycleInput(
    LifecycleOperation Operation,
    string AgentId,
    string? AgentName = null,
    string? Role = null,
    JsonElement? Definition = null);
