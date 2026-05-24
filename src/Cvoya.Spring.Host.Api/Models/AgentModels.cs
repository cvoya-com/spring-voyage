// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using System.Text.Json;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Lifecycle;

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
/// Each entry is the unit's stable Guid actor id (matching
/// <c>Address.Id</c>); the server resolves each through the directory
/// and rejects the whole request with 404 when any id does not map to
/// a registered unit. Wire form is the canonical 32-character no-dash
/// hex per <see cref="Cvoya.Spring.Core.Identifiers.GuidFormatter"/>.
/// </param>
/// <param name="DefinitionJson">
/// Optional agent-definition JSON document serialised as a string. Under
/// ADR-0038 the execution block carries the agent-runtime catalogue id
/// and a structured model selector (e.g.
/// <c>{"execution":{"image":"…","runtime":"spring-voyage","model":{"provider":"ollama","id":"llama3.2:3b"}}}</c>).
/// When supplied, the server parses it and persists the <see cref="JsonElement"/>
/// to <c>AgentDefinitions.Definition</c> so the execution layer can read
/// <see cref="Cvoya.Spring.Core.Execution.AgentExecutionConfig"/> from it.
/// Using a string on the wire keeps the Kiota-generated client surface flat —
/// the equivalent nested-object shape leaks Kiota's <c>UntypedNode</c> into
/// every caller.  Leaving it <c>null</c> produces the lightweight
/// directory-only agent shape older clients use.
/// </param>
public record CreateAgentRequest(
    string DisplayName,
    string Description,
    string? Role,
    IReadOnlyList<Guid> UnitIds,
    string? DefinitionJson = null);

/// <summary>
/// Response body representing an agent. Fields below <c>RegisteredAt</c>
/// come from the agent's own metadata (<see cref="AgentMetadata"/>) and
/// may be <c>null</c> when the agent has never set them. <c>Enabled</c> is
/// projected with a default of <c>true</c> when unset so UI callers can
/// treat it as non-nullable.
/// </summary>
/// <param name="Name">
/// The canonical 32-char no-dash hex form of <see cref="Id"/>, suitable for
/// use as a URL path segment (matches <see cref="UnitResponse.Name"/> per
/// #2114). The human-readable label lives on <see cref="DisplayName"/>.
/// </param>
/// <param name="DisplayName">The human-readable display name.</param>
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
/// <param name="LifecycleStatus">
/// The agent's current lifecycle state per the unified state machine
/// (<see cref="Cvoya.Spring.Core.Lifecycle.LifecycleStatus"/>). <c>null</c>
/// when the GET could not read the actor's lifecycle row — treat that as
/// no signal rather than implicitly any specific state. Wire form is the
/// PascalCase enum name as a string (e.g. <c>"Draft"</c>, <c>"Running"</c>) so
/// the generated <c>LifecycleStatus</c> enum on the client (<see cref="UnitResponse.Status"/>
/// already emits the same shape) round-trips cleanly. A string field is
/// used rather than the strongly-typed enum to avoid polluting the
/// shared <c>LifecycleStatus</c> schema with a <c>null</c> member when
/// the property is nullable (#2388 / #2156).
/// </param>
/// <param name="LifecycleError">
/// Diagnostic message persisted alongside an <c>Error</c> lifecycle row.
/// <c>null</c> when <see cref="LifecycleStatus"/> is not <c>Error</c> or
/// when the lifecycle could not be read. Added by #2156.
/// </param>
/// <param name="ParentUnitId">
/// Guid of the primary parent unit (32-char hex, no dashes — the wire form
/// that the rest of the API uses as a path parameter). <c>null</c> when the
/// agent has no memberships. Companion to <see cref="ParentUnit"/>, which
/// carries the unit's display name for human-readable surfaces;
/// <see cref="ParentUnitId"/> is what callers should pass back as the
/// <c>{id}</c> on <c>/api/v1/tenant/units/{id}/...</c> routes (#2250).
/// </param>
/// <param name="Instructions">
/// The agent's own <c>instructions</c> slot, read from the persisted
/// agent-definition JSON (ADR-0043). <c>null</c> when the agent has no
/// instructions of its own — the dispatcher merges the parent unit's
/// instructions in at dispatch time, but this field carries only what
/// is persisted on the agent row so the portal can render the
/// inherited overlay separately. Added by #2293.
/// </param>
/// <param name="EffectiveTools">
/// Flat list of tools effectively granted to this agent, sourced from
/// <see cref="Cvoya.Spring.Core.Skills.IToolGrantResolver"/> and merged
/// across the four provenance tiers (platform / connector / image /
/// explicit). Surfaced on the wire so the portal's Tools sub-tab
/// (#2337) can render the three-tier layout without re-deriving the
/// grant set. Empty list when the resolver returns nothing — the field
/// is non-null on the wire to keep client code branchless.
/// </param>
/// <param name="ExecutionImage">
/// The container image tag (e.g. <c>acme/agent:v1.2</c>) read from the
/// persisted <c>execution.image</c> slot via
/// <see cref="Cvoya.Spring.Core.Execution.IAgentDefinitionProvider"/> —
/// the same path the dispatcher uses, so the merge with the parent
/// unit's defaults (#601 / #603) flows through automatically. <c>null</c>
/// when neither the agent nor its primary parent unit declares an image.
/// Surfaced so the portal's Tools sub-tab Image section (#2348) can
/// render the tag rather than the digest-suffixed provenance string.
/// </param>
/// <param name="SystemPromptMode">
/// The <b>resolved</b> system-prompt mode (#2692 / #2691 / #2667). Lower-case
/// enum literal — <c>"append"</c> or <c>"replace"</c>. Comes from the
/// agent → unit → <c>append</c> cascade applied by the dispatcher's
/// definition provider, so the portal can render the effective value
/// without re-running the merge. <c>null</c> only when the agent has no
/// execution block at all (the cascade has nothing to resolve against).
/// </param>
/// <param name="DeclaredSystemPromptMode">
/// The <b>raw declared</b> system-prompt mode from the agent's own
/// execution block, before the inheritance cascade. Lower-case enum
/// literal or <c>null</c> when the agent did not declare its own value
/// (in which case the resolved <see cref="SystemPromptMode"/> reflects
/// the parent unit's default, or the platform fallback). The portal uses
/// the (declared, resolved) pair to distinguish "explicitly set" from
/// "inherited" without a second round-trip.
/// </param>
public record AgentResponse(
    Guid Id,
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
    string? ParentUnitId = null,
    string? HostingMode = null,
    string? InitiativeLevel = null,
    string? LifecycleStatus = null,
    string? LifecycleError = null,
    string? Instructions = null,
    IReadOnlyList<EffectiveToolResponse>? EffectiveTools = null,
    string? ExecutionImage = null,
    string? SystemPromptMode = null,
    string? DeclaredSystemPromptMode = null);

/// <summary>
/// Request body for <c>PATCH /api/v1/agents/{id}</c>. All fields optional;
/// <c>null</c> means "leave unchanged." <c>ParentUnit</c> is intentionally
/// absent — changing containment goes through the unit's assign / unassign
/// endpoints so the <c>agent.ParentUnit</c> ↔ <c>unit.Members</c> invariant
/// is maintained in one place.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DisplayName"/>, <see cref="Description"/>, and <see cref="Role"/>
/// live on the directory entity and are routed through
/// <see cref="Cvoya.Spring.Core.Directory.IDirectoryService.UpdateEntryAsync"/> —
/// mirrors the same split <see cref="UpdateUnitRequest"/> uses for unit metadata.
/// <see cref="DisplayName"/> passes the same validation gate
/// (<c>DisplayNameProblems</c>) the create flow uses.
/// </para>
/// <para>
/// <see cref="Instructions"/> is a tri-state slot per ADR-0043: omitting the
/// property leaves the agent's <c>instructions</c> unchanged; an explicit JSON
/// <c>null</c> clears it; a string replaces it. The DTO collapses absent /
/// explicit-null at deserialization, so the endpoint inspects the raw JSON
/// body to distinguish the two — callers that PATCH this record directly
/// (e.g. typed C# clients) will get the "leave unchanged" semantics for
/// <c>Instructions = null</c>; callers that send an explicit
/// <c>"instructions": null</c> on the wire get the clear semantics.
/// </para>
/// <para>
/// <see cref="SystemPromptMode"/> (#2692 / #2691 / #2667) uses the same
/// tri-state shape as <see cref="Instructions"/>: omitting the property
/// leaves the agent's persisted <c>execution.system_prompt_mode</c> slot
/// alone; an explicit JSON <c>null</c> clears it (so the cascade falls
/// back to the parent unit / platform default); a string replaces it.
/// Accepted literals are <c>"append"</c> and <c>"replace"</c>
/// (case-insensitive); other values are rejected with a 400.
/// </para>
/// </remarks>
public record UpdateAgentMetadataRequest(
    string? DisplayName = null,
    string? Description = null,
    string? Role = null,
    string? Model = null,
    string? Specialty = null,
    bool? Enabled = null,
    AgentExecutionMode? ExecutionMode = null,
    string? Instructions = null,
    string? SystemPromptMode = null);

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
/// A single entry in the equipped-skills surface returned by
/// <c>GET /api/v1/tenant/{units|agents}/{id}/skills</c> (#2360). Carries
/// the canonical package + skill coordinates, a short prompt summary
/// suitable for UI listings, and the bundle's required-tool list so
/// callers can render the operator's effective tool surface without a
/// second round-trip.
/// </summary>
/// <param name="PackageName">Package the skill was resolved from (e.g. <c>spring-voyage/software-engineering</c>).</param>
/// <param name="SkillName">Skill identifier within the package.</param>
/// <param name="PromptSummary">
/// Short excerpt of the resolved prompt body — the first line (or the
/// first 200 characters, whichever comes first) — suitable for table
/// rows in operator UIs. Callers that need the full prompt can read it
/// from the package contents directly.
/// </param>
/// <param name="RequiredTools">
/// Tool requirements declared by the bundle. Empty when the bundle is
/// prompt-only. The shape matches what the bundle declared at install
/// time; runtime tool-availability is a separate concern surfaced
/// through the tool-grant resolver.
/// </param>
public record EquippedSkillEntry(
    string PackageName,
    string SkillName,
    string PromptSummary,
    IReadOnlyList<EquippedSkillToolRequirement> RequiredTools);

/// <summary>
/// Tool requirement declared by a skill bundle. Flat wire shape; the
/// JSON-schema field is omitted from this listing surface — callers
/// that need the full schema fetch the bundle directly.
/// </summary>
public record EquippedSkillToolRequirement(
    string Name,
    string Description,
    bool Optional);

/// <summary>
/// Response body for <c>GET /api/v1/tenant/{units|agents}/{id}/skills</c>
/// (#2360). The list reflects declaration order — the first entry
/// renders first in the assembled prompt.
/// </summary>
public record EquippedSkillsResponse(IReadOnlyList<EquippedSkillEntry> Skills);

/// <summary>
/// Request body for <c>POST /api/v1/tenant/{units|agents}/{id}/skills</c>
/// (#2360). Equips a single bundle. Idempotent on
/// <c>(packageName, skillName)</c> — re-posting an already-equipped pair
/// refreshes the persisted prompt + required-tools snapshot without
/// reordering.
/// </summary>
public record EquipSkillRequest(string PackageName, string SkillName);

/// <summary>
/// Response body for <c>GET /api/v1/tenant/agents/{id}/runtime-status</c>
/// and <c>GET /api/v1/tenant/units/{id}/runtime-status</c> (#2100).
/// Surfaces the four-state runtime indicator the portal renders next to
/// every agent / unit name (engagement timeline, member rosters, drawer
/// panels, mention chips).
/// </summary>
/// <param name="Status">
/// One of <c>idle</c>, <c>busy</c>, <c>queued</c>, or <c>unavailable</c>.
/// Lower-case wire form so the portal's status-chip switch can match
/// without a normalisation step. Maps from
/// <see cref="AgentRuntimeStatus"/>.
/// </param>
/// <param name="LastUpdated">
/// UTC timestamp the snapshot was taken at the actor. Polling clients
/// surface staleness from this. The endpoint takes the actor's
/// <c>ObservedAt</c> when available; on the unavailable path it stamps
/// the request time so the wire field is never absent.
/// </param>
/// <param name="InFlightThreadCount">
/// Number of per-thread channels with a dispatcher currently running.
/// <c>0</c> for units (no per-thread channels yet) and for idle agents.
/// </param>
/// <param name="QueuedMessageCount">
/// Total messages queued behind the in-flight heads across every channel.
/// <c>0</c> when no head-of-line victims exist.
/// </param>
public record AgentRuntimeStatusResponse(
    string Status,
    DateTimeOffset LastUpdated,
    int InFlightThreadCount,
    int QueuedMessageCount);
