// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Response body for <c>GET /api/v1/units/{id}/connector</c>. A pointer to
/// the connector typed config for this unit — intentionally narrow, so the
/// generic Host.Api response stays stable regardless of how the connector
/// shapes its payload.
/// </summary>
/// <param name="TypeId">The connector type id persisted for this binding.</param>
/// <param name="TypeSlug">
/// The connector type slug, or <c>unknown</c> when the binding references a
/// connector type that is no longer registered (e.g. its package was
/// removed). Returning a placeholder rather than 404 lets operators see
/// orphan bindings.
/// </param>
/// <param name="ConfigUrl">URL of the unit's typed config document.</param>
/// <param name="ActionsBaseUrl">Base URL for the owning connector's actions.</param>
public record UnitConnectorPointerResponse(
    Guid TypeId,
    string TypeSlug,
    string ConfigUrl,
    string ActionsBaseUrl);

/// <summary>
/// Response row for <c>GET /api/v1/connectors/{slugOrId}/bindings</c> (#520).
/// One entry per unit that is currently bound to the requested connector type.
/// Bundles the unit identity with the same pointer fields that
/// <see cref="UnitConnectorPointerResponse"/> carries so the portal and CLI
/// can render a "units bound to this connector" list in a single round-trip
/// instead of fanning out a <c>GET /api/v1/units/{id}/connector</c> per unit.
/// </summary>
/// <param name="UnitId">Canonical unit id (directory path segment).</param>
/// <param name="UnitName">The unit's name (unique identifier on the wire).</param>
/// <param name="UnitDisplayName">Human-facing unit display name.</param>
/// <param name="TypeId">The connector type id persisted for this binding.</param>
/// <param name="TypeSlug">
/// The connector type slug, or <c>unknown</c> when the binding references a
/// connector type that is no longer registered (parity with
/// <see cref="UnitConnectorPointerResponse"/>).
/// </param>
/// <param name="ConfigUrl">URL of the unit's typed config document.</param>
/// <param name="ActionsBaseUrl">Base URL for the owning connector's actions.</param>
public record ConnectorUnitBindingResponse(
    string UnitId,
    string UnitName,
    string UnitDisplayName,
    Guid TypeId,
    string TypeSlug,
    string ConfigUrl,
    string ActionsBaseUrl);

/// <summary>
/// Uniform response body for the tenant-scoped connector endpoints — the
/// union of the connector's type-descriptor fields (from the registry)
/// with the tenant install metadata (from <c>tenant_connector_installs</c>).
/// Returned by <c>GET /api/v1/connectors</c>,
/// <c>GET /api/v1/connectors/{slugOrId}</c>,
/// <c>POST /api/v1/connectors/{slugOrId}/install</c>, and
/// <c>PATCH /api/v1/connectors/{slugOrId}/config</c>. The list and get
/// endpoints are tenant-scoped: they only surface connectors that are
/// currently installed on the caller's tenant (see issue #714).
/// </summary>
/// <param name="TypeId">Stable connector identity from <c>IConnectorType.TypeId</c>.</param>
/// <param name="TypeSlug">URL-safe slug from <c>IConnectorType.Slug</c>.</param>
/// <param name="DisplayName">Human-facing display name.</param>
/// <param name="Description">Short description used by the wizard and unit-config UI.</param>
/// <param name="ConfigUrl">
/// Template for the per-unit typed config endpoint, containing a
/// <c>{unitId}</c> placeholder the client substitutes at call time.
/// </param>
/// <param name="ActionsBaseUrl">
/// Base URL under which the connector's typed actions are mapped. Clients
/// append <c>/{actionName}</c> to invoke a specific action.
/// </param>
/// <param name="ConfigSchemaUrl">
/// URL of the connector's JSON Schema describing its per-unit config body.
/// Useful for dynamic form generation.
/// </param>
/// <param name="InstalledAt">Timestamp when the connector was first installed on the tenant.</param>
/// <param name="UpdatedAt">Timestamp when the install row was last updated.</param>
/// <param name="Config">
/// Opaque per-tenant config payload. <c>null</c> for connectors with no
/// tenant-level configuration.
/// </param>
public record InstalledConnectorResponse(
    Guid TypeId,
    string TypeSlug,
    string DisplayName,
    string Description,
    string ConfigUrl,
    string ActionsBaseUrl,
    string ConfigSchemaUrl,
    DateTimeOffset InstalledAt,
    DateTimeOffset UpdatedAt,
    System.Text.Json.JsonElement? Config);

/// <summary>
/// Request body for <c>POST /api/v1/connectors/{slugOrId}/install</c>.
/// </summary>
/// <param name="Config">
/// Opaque tenant-level config payload to persist. <c>null</c> for an empty
/// payload — connectors with no tenant-level configuration should send
/// this or omit the body entirely.
/// </param>
public record ConnectorInstallRequest(System.Text.Json.JsonElement? Config);