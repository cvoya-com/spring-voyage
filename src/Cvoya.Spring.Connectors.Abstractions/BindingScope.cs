// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connectors;

/// <summary>
/// The scope at which a connector binds. Determines whether the binding
/// row lives on the per-unit <c>unit_connector_bindings</c> table (the
/// historical default) or the per-tenant <c>tenant_connector_bindings</c>
/// table introduced by
/// <see href="https://github.com/cvoya-com/spring-voyage/blob/main/docs/decisions/0061-slack-connector-oss-shape.md">ADR-0061</see>
/// §1, and which endpoint shape the host exposes —
/// <c>/api/v1/tenant/connectors/{slug}/units/{unitId}/config</c> for
/// <see cref="Unit"/> connectors, or
/// <c>/api/v1/tenant/connectors/{slug}/binding</c> (singular, no unit
/// segment) for <see cref="Tenant"/> connectors.
/// </summary>
/// <remarks>
/// <para>
/// Most connectors are inherently per-unit (GitHub repository wiring,
/// arxiv default categories, web-search provider selection) and bind at
/// the <see cref="Unit"/> scope. Connectors whose external resource is
/// naturally workspace-shaped (a Slack workspace, a calendar account, a
/// shared mailbox) bind at the <see cref="Tenant"/> scope so the operator
/// installs the app once per tenant rather than once per unit. ADR-0061
/// §1 explains the choice in detail for the Slack connector.
/// </para>
/// <para>
/// The contract is enum-shaped (not a boolean) so future scopes (e.g. an
/// agent-scoped binding) can be added without re-coding existing
/// connectors. ADR-0061 §7.7 names the requirement that the
/// tenant-scoped binding store be generic — the
/// <see cref="Tenant"/> value here is therefore not Slack-specific.
/// </para>
/// </remarks>
public enum BindingScope
{
    /// <summary>
    /// Per-unit binding. The binding lives on
    /// <c>unit_connector_bindings</c> keyed by
    /// <c>(tenant_id, unit_id)</c> and is addressed through
    /// <c>IUnitConnectorBindingStore</c>. Endpoints take the unit id in
    /// the URL.
    /// </summary>
    Unit = 0,

    /// <summary>
    /// Per-tenant binding. The binding lives on
    /// <c>tenant_connector_bindings</c> keyed by
    /// <c>(tenant_id, connector_slug)</c> and is addressed through
    /// <c>ITenantConnectorBindingStore</c>. Endpoints take no unit
    /// segment — there is at most one binding per tenant.
    /// </summary>
    Tenant = 1,
}
