// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using System.Text.Json;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Units;

/// <summary>
/// Request body for creating a new unit.
/// </summary>
/// <param name="Name">The unique name for the unit.</param>
/// <param name="DisplayName">A human-readable display name.</param>
/// <param name="Description">A description of the unit's purpose.</param>
/// <param name="Model">An optional model identifier hint (e.g., default LLM).</param>
/// <param name="Color">An optional UI color hint used by the dashboard.</param>
/// <param name="ParentUnitIds">
/// The parent-unit memberships to establish for the new unit. Per the
/// review feedback on #744, every unit must either belong to at least
/// one parent unit OR be created with the explicit <paramref name="IsTopLevel"/>
/// flag set. The two options are mutually exclusive: neither → 400, both
/// → 400, unknown parent-unit id → 404. Each entry is the parent unit's
/// stable Guid actor id (matching <c>Address.Id</c>); the server
/// resolves each through the directory and rejects the whole request
/// with 404 when any id does not map to a registered unit. Wire form
/// is the canonical 32-character no-dash hex per
/// <see cref="Cvoya.Spring.Core.Identifiers.GuidFormatter"/>.
/// </param>
/// <param name="IsTopLevel">
/// When <c>true</c>, marks the unit as a top-level (tenant-parented)
/// unit — its parent is the tenant itself. Mutually exclusive with a
/// non-empty <paramref name="ParentUnitIds"/>. Persisted to the unit
/// definition row so the parent-required invariant can distinguish
/// "deliberately tenant-parented" from "orphaned in transit."
/// </param>
public record CreateUnitRequest(
    string Name,
    string DisplayName,
    string Description,
    string? Model = null,
    string? Color = null,
    UnitConnectorBindingRequest? Connector = null,
    string? Hosting = null,
    IReadOnlyList<Guid>? ParentUnitIds = null,
    bool? IsTopLevel = null);

/// <summary>
/// Request body for updating mutable unit metadata. All fields are optional;
/// <c>null</c> means "leave the existing value untouched".
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DisplayName"/>, <see cref="Description"/>, and <see cref="Role"/>
/// live on the directory entity and are routed through
/// <see cref="Cvoya.Spring.Core.Directory.IDirectoryService.UpdateEntryAsync"/>.
/// <see cref="DisplayName"/> passes the same validation gate (<c>DisplayNameProblems</c>)
/// the create flow uses. Mirrors the same split <see cref="UpdateAgentMetadataRequest"/>
/// uses for agent metadata.
/// </para>
/// <para>
/// <see cref="Instructions"/> is a tri-state slot per ADR-0043: omitting the
/// property leaves the unit's <c>instructions</c> unchanged; an explicit
/// JSON <c>null</c> clears it; a string replaces it. The DTO collapses
/// absent / explicit-null at deserialization, so the endpoint inspects the
/// raw JSON body to distinguish the two.
/// </para>
/// <para>
/// <see cref="Specialty"/> / <see cref="Enabled"/> / <see cref="ExecutionMode"/>
/// were added in #2341 for unit/agent parity per <c>units-vs-agents.md</c>
/// (only cloning is documented as agent-only; everything else applies to both).
/// They persist on the unit live-config row, same shape as the agent equivalents.
/// </para>
/// </remarks>
/// <param name="DisplayName">The new display name, or <c>null</c> to leave unchanged.</param>
/// <param name="Description">The new description, or <c>null</c> to leave unchanged.</param>
/// <param name="Model">The new model hint, or <c>null</c> to leave unchanged.</param>
/// <param name="Color">The new UI color hint, or <c>null</c> to leave unchanged.</param>
/// <param name="Hosting">The new hosting hint, or <c>null</c> to leave unchanged.</param>
/// <param name="Instructions">The new instructions value (set / clear / leave-alone tri-state).</param>
/// <param name="Role">The new role identifier (used by multicast resolution), or <c>null</c> to leave unchanged. Added in #2341.</param>
/// <param name="Specialty">The new specialty label, or <c>null</c> to leave unchanged. Added in #2341.</param>
/// <param name="Enabled">The new enabled flag, or <c>null</c> to leave unchanged. Added in #2341.</param>
/// <param name="ExecutionMode">The new execution mode, or <c>null</c> to leave unchanged. Added in #2341.</param>
public record UpdateUnitRequest(
    string? DisplayName = null,
    string? Description = null,
    string? Model = null,
    string? Color = null,
    string? Hosting = null,
    string? Instructions = null,
    string? Role = null,
    string? Specialty = null,
    bool? Enabled = null,
    AgentExecutionMode? ExecutionMode = null);

/// <summary>
/// Response body representing a unit.
/// </summary>
/// <param name="Id">The unique actor identifier.</param>
/// <param name="Name">The unit's name (address path).</param>
/// <param name="DisplayName">The human-readable display name.</param>
/// <param name="Description">A description of the unit.</param>
/// <param name="RegisteredAt">The timestamp when the unit was registered.</param>
/// <param name="Status">The current lifecycle status of the unit.</param>
/// <param name="Model">An optional model identifier hint, if set.</param>
/// <param name="Color">An optional UI color hint, if set.</param>
/// <param name="Hosting">Optional hosting hint.</param>
/// <param name="LastValidationError">Structured outcome of the most recent failed validation run, or <c>null</c> when the most recent run succeeded or the unit has never been validated.</param>
/// <param name="LastValidationRunId">Dapr workflow instance id of the most recent validation run. Null until the first run.</param>
/// <param name="Role">Optional role identifier used by multicast resolution (mirrors <see cref="AgentResponse.Role"/>). Added in #2341.</param>
/// <param name="Specialty">Optional specialty label consumed by orchestration strategies (mirrors <see cref="AgentResponse.Specialty"/>). Added in #2341.</param>
/// <param name="Enabled">Whether the unit participates in orchestration. Defaults to <c>true</c> (mirrors <see cref="AgentResponse.Enabled"/>). Added in #2341.</param>
/// <param name="ExecutionMode">How the unit participates in dispatch (mirrors <see cref="AgentResponse.ExecutionMode"/>). Added in #2341.</param>
/// <remarks>
/// ADR-0038: the standalone <c>Provider</c> slot is dropped — provider is
/// intrinsic to <c>execution.model.provider</c>. The execution tool slot
/// is derived from the catalogue via the unit's <c>execution.runtime</c>.
/// Use <c>GET /api/v1/tenant/units/{id}/execution</c> for the structured
/// view.
/// </remarks>
/// <param name="EffectiveTools">
/// Flat list of tools effectively granted to this unit, sourced from
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
/// the same path the dispatcher uses. <c>null</c> when the unit declares
/// no image. Surfaced so the portal's Tools sub-tab Image section
/// (#2348) can render the tag rather than the digest-suffixed
/// provenance string.
/// </param>
public record UnitResponse(
    Guid Id,
    string Name,
    string DisplayName,
    string Description,
    DateTimeOffset RegisteredAt,
    LifecycleStatus Status,
    string? Model,
    string? Color,
    string? Hosting = null,
    ArtefactValidationError? LastValidationError = null,
    string? LastValidationRunId = null,
    string? Instructions = null,
    string? Role = null,
    string? Specialty = null,
    bool Enabled = true,
    AgentExecutionMode ExecutionMode = AgentExecutionMode.Auto,
    IReadOnlyList<EffectiveToolResponse>? EffectiveTools = null,
    string? ExecutionImage = null);

/// <summary>
/// Request body for adding a member to a unit.
/// </summary>
/// <param name="MemberAddress">The address of the member to add (e.g., agent://my-agent).</param>
public record AddMemberRequest(AddressDto MemberAddress);

/// <summary>
/// Request body for setting a human's permission level within a unit.
/// </summary>
/// <param name="Permission">The permission level (Viewer, Operator, Owner).</param>
/// <param name="Identity">An optional display name or identity string for the human.</param>
/// <param name="Notifications">Whether this human receives notifications. Defaults to true.</param>
public record SetHumanPermissionRequest(
    string Permission,
    string? Identity = null,
    bool? Notifications = null);

/// <summary>
/// Entry returned by <c>GET /api/v1/packages/templates</c>.
/// </summary>
/// <param name="Package">The package that owns the template.</param>
/// <param name="Name">The unit name declared by the template's YAML.</param>
/// <param name="Description">Optional human-readable description.</param>
/// <param name="Path">Repo-relative path to the template YAML (for display).</param>
public record UnitTemplateSummary(
    string Package,
    string Name,
    string? Description,
    string Path);

/// <summary>
/// Response body for <c>GET /api/v1/units/{id}</c>. Carries the unit
/// envelope plus the opaque <c>details</c> payload returned by the
/// unit actor's StatusQuery when that call succeeds (<c>null</c> when
/// the actor is unreachable or returns no details).
/// </summary>
public record UnitDetailResponse(UnitResponse Unit, System.Text.Json.JsonElement? Details);

/// <summary>
/// Response body for <c>POST /api/v1/units/{id}/start</c> and
/// <c>POST /api/v1/units/{id}/stop</c>. Returns the unit id and the
/// post-transition lifecycle status.
/// </summary>
public record UnitLifecycleResponse(Guid UnitId, LifecycleStatus Status);

/// <summary>
/// Response body for <c>PATCH /api/v1/units/{id}/humans/{humanId}/permissions</c>.
/// Returns the human id and the permission level that was set. <c>Permission</c>
/// is fully-qualified to avoid pulling <c>using Cvoya.Spring.Dapr.Actors</c>
/// into the Models layer for one type.
/// </summary>
public record SetHumanPermissionResponse(
    Guid HumanId,
    Cvoya.Spring.Dapr.Actors.PermissionLevel Permission);

/// <summary>
/// Response body for a force-delete that left some teardown steps in a
/// failed state. Returned with HTTP 200 (directory entry was removed) so
/// operators can see which subsystems need manual cleanup.
/// </summary>
public record UnitForceDeleteResponse(
    Guid UnitId,
    bool ForceDeleted,
    LifecycleStatus PreviousStatus,
    IReadOnlyList<string> TeardownFailures,
    string Message);

/// <summary>
/// Optional connector binding bundled into a unit-creation request so the
/// wizard can atomically create the unit AND bind it to a connector in a
/// single round-trip. Without this, the wizard has to take two calls (unit
/// create → connector PUT), which leaves a partially-configured unit behind
/// if the second call fails or the user abandons the flow.
/// </summary>
/// <remarks>
/// The unit-creation service validates that <paramref name="TypeId"/> matches
/// a registered connector and, if binding fails, rolls back the partial unit
/// by removing the directory entry. The entire exchange produces ProblemDetails
/// on the 4xx path.
/// </remarks>
/// <param name="TypeId">
/// The connector type id (matches <c>IConnectorType.TypeId</c>).
/// </param>
/// <param name="TypeSlug">
/// Optional convenience: the slug of the connector type. If
/// <paramref name="TypeId"/> is <c>Guid.Empty</c> the service resolves the
/// type via this slug instead. At least one of the two must be supplied.
/// </param>
/// <param name="Config">
/// The typed config payload the connector understands. The shape is dictated
/// by the target connector's <c>IConnectorType.ConfigType</c>; this layer
/// stays type-agnostic and forwards it verbatim to the connector's config
/// store.
/// </param>
public record UnitConnectorBindingRequest(
    Guid TypeId,
    string? TypeSlug,
    JsonElement Config);

/// <summary>
/// Response body for <c>GET /api/v1/units/{id}/readiness</c>. Describes
/// whether the unit is ready to leave Draft and what requirements are
/// missing.
/// </summary>
/// <param name="IsReady">True when the unit can be started.</param>
/// <param name="MissingRequirements">Labels for unsatisfied requirements (e.g. <c>"model"</c>).</param>
public record UnitReadinessResponse(bool IsReady, string[] MissingRequirements);

/// <summary>
/// Response body for <c>GET /api/v1/units/{id}/deployment</c>. Surfaces
/// the unit's lifecycle state in a deployment-shaped view so the portal's
/// Deployment tab can render start/stop controls without a separate unit
/// status query. A unit that is <see cref="LifecycleStatus.Running"/> is
/// considered <see cref="Running"/>; all other states map to
/// <c>Running = false</c>.
/// </summary>
/// <param name="Running">True when the unit status is <c>Running</c>.</param>
/// <param name="Status">The current <see cref="LifecycleStatus"/> label.</param>
public record UnitDeploymentResponse(bool Running, string Status);
