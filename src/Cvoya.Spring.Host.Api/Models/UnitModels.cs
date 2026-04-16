// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using System.Text.Json;

using Cvoya.Spring.Core.Units;

/// <summary>
/// Request body for creating a new unit.
/// </summary>
/// <param name="Name">The unique name for the unit.</param>
/// <param name="DisplayName">A human-readable display name.</param>
/// <param name="Description">A description of the unit's purpose.</param>
/// <param name="Model">An optional model identifier hint (e.g., default LLM).</param>
/// <param name="Color">An optional UI color hint used by the dashboard.</param>
public record CreateUnitRequest(
    string Name,
    string DisplayName,
    string Description,
    string? Model = null,
    string? Color = null,
    UnitConnectorBindingRequest? Connector = null,
    string? Tool = null,
    string? Provider = null,
    string? Hosting = null);

/// <summary>
/// Request body for updating mutable unit metadata. All fields are optional;
/// <c>null</c> means "leave the existing value untouched".
/// </summary>
/// <param name="DisplayName">The new display name, or <c>null</c> to leave unchanged.</param>
/// <param name="Description">The new description, or <c>null</c> to leave unchanged.</param>
/// <param name="Model">The new model hint, or <c>null</c> to leave unchanged.</param>
/// <param name="Color">The new UI color hint, or <c>null</c> to leave unchanged.</param>
public record UpdateUnitRequest(
    string? DisplayName = null,
    string? Description = null,
    string? Model = null,
    string? Color = null,
    string? Tool = null,
    string? Provider = null,
    string? Hosting = null);

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
public record UnitResponse(
    string Id,
    string Name,
    string DisplayName,
    string Description,
    DateTimeOffset RegisteredAt,
    UnitStatus Status,
    string? Model,
    string? Color,
    string? Tool = null,
    string? Provider = null,
    string? Hosting = null);

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
/// Request body for <c>POST /api/v1/units/from-yaml</c>. The caller supplies
/// the raw manifest plus optional overrides that take precedence over values
/// the manifest would otherwise supply.
/// </summary>
/// <param name="Yaml">Raw manifest YAML text (required).</param>
/// <param name="DisplayName">Optional override for the unit's display name.</param>
/// <param name="Color">Optional override for the unit's UI colour.</param>
/// <param name="Model">Optional override for the default model hint.</param>
public record CreateUnitFromYamlRequest(
    string Yaml,
    string? DisplayName = null,
    string? Color = null,
    string? Model = null,
    UnitConnectorBindingRequest? Connector = null,
    string? Tool = null,
    string? Provider = null,
    string? Hosting = null);

/// <summary>
/// Request body for <c>POST /api/v1/units/from-template</c>.
/// </summary>
/// <param name="Package">The package that owns the template (e.g. <c>software-engineering</c>).</param>
/// <param name="Name">The template's unit name (file basename without extension).</param>
/// <param name="DisplayName">Optional override for the unit's display name.</param>
/// <param name="Color">Optional override for the unit's UI colour.</param>
/// <param name="Model">Optional override for the default model hint.</param>
/// <param name="Connector">Optional connector binding to apply atomically with creation.</param>
/// <param name="UnitName">
/// Optional override for the created unit's <c>name</c> (address path). Without
/// this override the created unit takes its name verbatim from the template's
/// manifest <c>name</c> field, which means two clients instantiating the same
/// template collide on the unique-name constraint. Supply a caller-owned value
/// (e.g. a run-scoped id) to avoid the collision. When supplied, validated
/// the same way <see cref="CreateUnitRequest.Name"/> is validated on the
/// direct-create path, and surfaced as the unit's address path. See #325.
/// </param>
public record CreateUnitFromTemplateRequest(
    string Package,
    string Name,
    string? DisplayName = null,
    string? Color = null,
    string? Model = null,
    UnitConnectorBindingRequest? Connector = null,
    string? UnitName = null,
    string? Tool = null,
    string? Provider = null,
    string? Hosting = null);

/// <summary>
/// Response body for a unit created through the manifest-backed flows
/// (<c>/from-yaml</c> or <c>/from-template</c>). Layers non-fatal warnings
/// on top of <see cref="UnitResponse"/> so the wizard can surface them.
/// </summary>
/// <param name="Unit">The created unit.</param>
/// <param name="Warnings">Non-fatal warnings (e.g. unsupported manifest sections).</param>
/// <param name="MembersAdded">Number of members successfully wired up.</param>
public record UnitCreationResponse(
    UnitResponse Unit,
    IReadOnlyList<string> Warnings,
    int MembersAdded);

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
public record UnitLifecycleResponse(string UnitId, UnitStatus Status);

/// <summary>
/// Response body for <c>PATCH /api/v1/units/{id}/humans/{humanId}/permissions</c>.
/// Returns the human id and the permission level that was set. <c>Permission</c>
/// is fully-qualified to avoid pulling <c>using Cvoya.Spring.Dapr.Actors</c>
/// into the Models layer for one type.
/// </summary>
public record SetHumanPermissionResponse(
    string HumanId,
    Cvoya.Spring.Dapr.Actors.PermissionLevel Permission);

/// <summary>
/// Response body for a force-delete that left some teardown steps in a
/// failed state. Returned with HTTP 200 (directory entry was removed) so
/// operators can see which subsystems need manual cleanup.
/// </summary>
public record UnitForceDeleteResponse(
    string UnitId,
    bool ForceDeleted,
    UnitStatus PreviousStatus,
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