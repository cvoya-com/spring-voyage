// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Uniform response shape for <c>GET /api/v1/connectors</c> and
/// <c>GET /api/v1/connectors/{slugOrId}</c>. Non-polymorphic on purpose: every
/// connector reports the same envelope regardless of its typed config shape,
/// so clients can list connectors generically and drill into a specific one's
/// typed surface via <see cref="ConfigUrl"/> / <see cref="ActionsBaseUrl"/>.
/// </summary>
/// <param name="TypeId">
/// Stable identity. Persisted with every unit binding so renames of
/// <see cref="TypeSlug"/> never invalidate stored data.
/// </param>
/// <param name="TypeSlug">
/// URL-safe identifier (e.g. <c>github</c>). Used as the registry key on the
/// web side and as the <c>{slug}</c> segment in connector-owned routes.
/// </param>
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
public record ConnectorTypeResponse(
    Guid TypeId,
    string TypeSlug,
    string DisplayName,
    string Description,
    string ConfigUrl,
    string ActionsBaseUrl,
    string ConfigSchemaUrl);

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