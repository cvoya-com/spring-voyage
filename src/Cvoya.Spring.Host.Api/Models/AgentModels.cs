// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using System.Text.Json;

using Cvoya.Spring.Core.Agents;

/// <summary>
/// Request body for creating a new agent.
/// </summary>
/// <param name="Name">The unique name for the agent.</param>
/// <param name="DisplayName">A human-readable display name.</param>
/// <param name="Description">A description of the agent's purpose.</param>
/// <param name="Role">An optional role identifier for multicast resolution.</param>
/// <param name="UnitIds">
/// The unit memberships to establish for the new agent. Per #744 every
/// agent must belong to at least one unit at creation time — the server
/// rejects the request with 400 when this list is empty or omitted.
/// Each entry is a unit id (equivalent to the unit's <c>Address.Path</c>);
/// the server resolves each through the directory and rejects the whole
/// request with 404 when any id does not map to a registered unit.
/// </param>
/// <param name="DefinitionJson">
/// Optional agent-definition JSON document serialised as a string (e.g.
/// <c>{"execution":{"tool":"dapr-agent","image":"…","provider":"ollama","model":"llama3.2:3b"}}</c>).
/// When supplied, the server parses it and persists the <see cref="JsonElement"/>
/// to <c>AgentDefinitions.Definition</c> so the execution layer can read
/// <see cref="Cvoya.Spring.Core.Execution.AgentExecutionConfig"/> from it.
/// Using a string on the wire keeps the Kiota-generated client surface flat —
/// the equivalent nested-object shape leaks Kiota's <c>UntypedNode</c> into
/// every caller.  Leaving it <c>null</c> produces the lightweight
/// directory-only agent shape older clients use.
/// </param>
public record CreateAgentRequest(
    string Name,
    string DisplayName,
    string Description,
    string? Role,
    IReadOnlyList<string> UnitIds,
    string? DefinitionJson = null);

/// <summary>
/// Response body representing an agent. Fields below <c>RegisteredAt</c>
/// come from the agent's own metadata (<see cref="AgentMetadata"/>) and
/// may be <c>null</c> when the agent has never set them. <c>Enabled</c> is
/// projected with a default of <c>true</c> when unset so UI callers can
/// treat it as non-nullable.
/// </summary>
/// <param name="HostingMode">
/// The agent's declared hosting mode (<c>ephemeral</c> or <c>persistent</c>),
/// read from the agent's persisted <c>execution.hosting</c> field. <c>null</c>
/// when the agent has no execution block or the block carries no hosting
/// declaration — the dispatcher defaults to ephemeral in that case.
/// Added by #572.
/// </param>
/// <param name="InitiativeLevel">
/// The agent's current effective initiative level as resolved by the initiative
/// engine (<c>passive</c>, <c>attentive</c>, <c>proactive</c>, or
/// <c>autonomous</c>). <c>null</c> when the level could not be resolved
/// (e.g. policy store unavailable — fail-open on the list path). Added by #573.
/// </param>
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
    string? ParentUnit,
    string? HostingMode = null,
    string? InitiativeLevel = null);

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

/// <summary>
/// Response body for <c>GET /api/v1/agents/{id}</c> when the StatusQuery to
/// the actor succeeds. Combines the directory-level <see cref="AgentResponse"/>
/// with the opaque runtime status payload returned by the actor. When the
/// StatusQuery fails, the endpoint falls back to returning the
/// <see cref="AgentResponse"/> alone. <c>Deployment</c> is populated for
/// persistent agents that have a current container-level deployment tracked
/// in <c>PersistentAgentRegistry</c> (#396); <c>null</c> for ephemeral agents
/// or persistent agents that have been undeployed.
/// </summary>
/// <param name="Status">
/// The actor's runtime status payload, serialised as a JSON string. Using a
/// string on the wire keeps the Kiota-generated client surface flat — the
/// equivalent <see cref="JsonElement"/> shape lowers to an empty-schema
/// <c>oneOf</c> in OpenAPI and trips Kiota's composed-type serialiser (issue
/// #1000). The CLI's <c>agent status</c> verb currently reads only
/// <c>Agent.*</c> and <c>Deployment.*</c> columns; consumers that need the
/// actor status can <c>JsonDocument.Parse(Status)</c>. Mirrors the same
/// convention used for <see cref="CreateAgentRequest.DefinitionJson"/>.
/// </param>
public record AgentDetailResponse(
    AgentResponse Agent,
    string? Status,
    PersistentAgentDeploymentResponse? Deployment = null);

/// <summary>
/// An entry in the platform-wide skill catalog returned by
/// <c>GET /api/v1/skills</c>. Each entry corresponds to one tool exposed
/// by some registered <c>ISkillRegistry</c>.
/// </summary>
/// <param name="Name">The tool name (e.g., <c>github_create_pull_request</c>). Unique across registries.</param>
/// <param name="Description">Human-readable description shown in the UI.</param>
/// <param name="Registry">Short identifier of the registry that owns the tool (e.g., <c>github</c>). Used for grouping in the UI.</param>
public record SkillCatalogEntry(string Name, string Description, string Registry);

/// <summary>
/// Response body for <c>GET /api/v1/agents/{id}/skills</c>. Returns the
/// agent's configured skill list verbatim; an empty list is meaningful
/// (agent is explicitly disabled from every tool) and distinct from a
/// 404 (agent does not exist).
/// </summary>
public record AgentSkillsResponse(IReadOnlyList<string> Skills);

/// <summary>
/// Request body for <c>PUT /api/v1/agents/{id}/skills</c>. Full replacement
/// of the agent's skill list — pass the new complete list. An empty list
/// clears the configuration; it is not treated as "leave alone."
/// </summary>
public record SetAgentSkillsRequest(IReadOnlyList<string> Skills);