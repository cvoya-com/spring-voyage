// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connectors;

/// <summary>
/// Connector-agnostic seam for "does any binding of this connector type in
/// any OTHER tenant carry the supplied addressing fingerprint?". Drives the
/// cross-tenant rejection per ADR-0047 §10: once an inbound webhook
/// payload carries no tenant signal, two tenants both claiming the same
/// addressing fingerprint cannot be disambiguated, so binding-create
/// refuses to land the second one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a probe, not a unique index.</b> The binding row stores the
/// connector-specific addressing fields inside an opaque <c>jsonb</c>
/// column — the platform deliberately does not enumerate them so the
/// connector contract stays connector-agnostic (ADR-0045). A unique
/// index would require the platform to know which JSON fields carry
/// the fingerprint per connector; a callback the connector invokes from
/// its binding-create endpoint keeps the knowledge local to the
/// connector while still letting the EF tier execute the cross-tenant
/// query with a clean SQL plan.
/// </para>
/// <para>
/// <b>Fingerprint shape.</b> The fingerprint is a free-form
/// connector-defined string; for GitHub it is the qualified
/// <c>owner/repo</c> form. The implementation does a case-insensitive
/// match on the connector's <c>UnitGitHubConfig.Repo</c> column
/// (extracted from the binding's typed config). Connectors that
/// register their own probe implementations decide their own
/// fingerprint shape.
/// </para>
/// </remarks>
public interface IConnectorBindingCrossTenantProbe
{
    /// <summary>
    /// Returns <c>true</c> when at least one binding for
    /// <paramref name="connectorTypeId"/> in a tenant other than the
    /// current one carries the supplied <paramref name="fingerprint"/>;
    /// <c>false</c> otherwise.
    /// </summary>
    /// <param name="connectorTypeId">The connector type to probe.</param>
    /// <param name="fingerprint">
    /// The connector-defined addressing fingerprint (e.g.
    /// <c>owner/repo</c> for GitHub). Matched case-insensitively.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// <c>true</c> when a cross-tenant collision exists; <c>false</c>
    /// otherwise. The same fingerprint within the current tenant is NOT
    /// a collision per ADR-0047 §10 — in-tenant fan-out is the supported
    /// configuration.
    /// </returns>
    Task<bool> HasCrossTenantBindingAsync(
        Guid connectorTypeId,
        string fingerprint,
        CancellationToken cancellationToken = default);
}
