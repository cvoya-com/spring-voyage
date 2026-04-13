// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

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
    string? Color = null);

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
    string? Color = null);

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
    string? Color);

/// <summary>
/// Request body for adding a member to a unit.
/// </summary>
/// <param name="MemberAddress">The address of the member to add (e.g., agent://my-agent).</param>
public record AddMemberRequest(AddressDto MemberAddress);

/// <summary>
/// Request body for binding a unit to a GitHub repository. The platform
/// registers a webhook on the configured repo when the unit starts and
/// deletes it when the unit stops.
/// </summary>
/// <param name="Owner">The repository owner (user or organization login).</param>
/// <param name="Repo">The repository name.</param>
public record SetUnitGitHubConfigRequest(string Owner, string Repo);

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
    string? Model = null);

/// <summary>
/// Request body for <c>POST /api/v1/units/from-template</c>.
/// </summary>
/// <param name="Package">The package that owns the template (e.g. <c>software-engineering</c>).</param>
/// <param name="Name">The template's unit name (file basename without extension).</param>
/// <param name="DisplayName">Optional override for the unit's display name.</param>
/// <param name="Color">Optional override for the unit's UI colour.</param>
/// <param name="Model">Optional override for the default model hint.</param>
public record CreateUnitFromTemplateRequest(
    string Package,
    string Name,
    string? DisplayName = null,
    string? Color = null,
    string? Model = null);

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
/// Response body for <c>PUT /api/v1/units/{id}/github</c>. Returns the
/// unit id and the GitHub configuration that was applied.
/// </summary>
public record SetUnitGitHubConfigResponse(string UnitId, UnitGitHubConfig GitHub);

/// <summary>
/// Unit connector configuration surfaced by <c>GET</c>/<c>PUT
/// /api/v1/units/{id}/connector</c>. Shaped as a discriminated envelope so
/// new connector types (Slack, Linear, ...) can be added without breaking the
/// route contract. Only the fields relevant to <see cref="Type"/> will be
/// populated — e.g. <see cref="Repo"/> / <see cref="AppInstallationId"/> /
/// <see cref="WebhookId"/> are GitHub-specific.
/// </summary>
/// <param name="Type">The connector type discriminator (e.g. <c>github</c>).</param>
/// <param name="Repo">The GitHub repository the unit is bound to (when <see cref="Type"/> is <c>github</c>).</param>
/// <param name="Events">Webhook events the unit subscribes to.</param>
/// <param name="AppInstallationId">The GitHub App installation id powering the binding.</param>
/// <param name="WebhookId">The id of the repository webhook registered on /start, when known.</param>
public record UnitConnectorResponse(
    string Type,
    UnitConnectorRepo? Repo,
    IReadOnlyList<string> Events,
    long? AppInstallationId,
    long? WebhookId);

/// <summary>
/// Repository descriptor embedded in <see cref="UnitConnectorResponse"/>.
/// Kept as a named type rather than an anonymous tuple so it can round-trip
/// cleanly through OpenAPI and the generated TypeScript client.
/// </summary>
public record UnitConnectorRepo(string Owner, string Name);

/// <summary>
/// Request body for <c>PUT /api/v1/units/{id}/connector</c>. Mirrors
/// <see cref="UnitConnectorResponse"/> minus read-only fields (<c>WebhookId</c>
/// is managed by the /start and /stop handlers). A <c>null</c>
/// <see cref="Events"/> falls back to the connector's default event set.
/// </summary>
/// <param name="Type">The connector type discriminator; only <c>github</c> is supported today.</param>
/// <param name="Repo">The GitHub repository to bind the unit to.</param>
/// <param name="Events">Webhook event names to subscribe to.</param>
/// <param name="AppInstallationId">The chosen GitHub App installation id.</param>
public record SetUnitConnectorRequest(
    string Type,
    UnitConnectorRepo? Repo,
    IReadOnlyList<string>? Events = null,
    long? AppInstallationId = null);

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