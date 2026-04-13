// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using Cvoya.Spring.Core.Agents;

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
/// Response body representing an agent. Fields below <c>RegisteredAt</c>
/// come from the agent's own metadata (<see cref="AgentMetadata"/>) and
/// may be <c>null</c> when the agent has never set them. <c>Enabled</c> is
/// projected with a default of <c>true</c> when unset so UI callers can
/// treat it as non-nullable.
/// </summary>
public record AgentResponse(
    string Id,
    string Name,
    string DisplayName,
    string Description,
    string? Role,
    DateTimeOffset RegisteredAt,
    string? Model,
    string? Specialty,
    bool Enabled,
    AgentExecutionMode ExecutionMode,
    string? ParentUnit);

/// <summary>
/// Request body for <c>PATCH /api/v1/agents/{id}</c>. All fields optional;
/// <c>null</c> means "leave unchanged." <c>ParentUnit</c> is intentionally
/// absent — changing containment goes through the unit's assign / unassign
/// endpoints so the <c>agent.ParentUnit</c> ↔ <c>unit.Members</c> invariant
/// is maintained in one place.
/// </summary>
public record UpdateAgentMetadataRequest(
    string? Model = null,
    string? Specialty = null,
    bool? Enabled = null,
    AgentExecutionMode? ExecutionMode = null);