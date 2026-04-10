// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Request body for creating a new agent.
/// </summary>
/// <param name="Name">The unique name for the agent.</param>
/// <param name="DisplayName">A human-readable display name.</param>
/// <param name="Description">A description of the agent's purpose.</param>
/// <param name="Role">An optional role identifier for multicast resolution.</param>
public record CreateAgentRequest(
    string Name,
    string DisplayName,
    string Description,
    string? Role);

/// <summary>
/// Response body representing an agent.
/// </summary>
/// <param name="Id">The unique actor identifier.</param>
/// <param name="Name">The agent's name (address path).</param>
/// <param name="DisplayName">The human-readable display name.</param>
/// <param name="Description">A description of the agent.</param>
/// <param name="Role">The agent's role, if any.</param>
/// <param name="RegisteredAt">The timestamp when the agent was registered.</param>
public record AgentResponse(
    string Id,
    string Name,
    string DisplayName,
    string Description,
    string? Role,
    DateTimeOffset RegisteredAt);
