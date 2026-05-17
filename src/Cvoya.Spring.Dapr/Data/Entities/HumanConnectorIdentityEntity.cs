// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persisted mapping row that binds a stable human UUID to a connector-native
/// user id (e.g. a GitHub login). One row per (tenant, connector, user_id);
/// a single human may carry multiple rows (one per connector). Backs
/// <see cref="Cvoya.Spring.Core.Security.IHumanConnectorIdentityResolver"/>
/// per #2408 / the v0.1 dogfooding plan.
/// </summary>
/// <remarks>
/// <para>
/// The unique invariant is <c>(tenant_id, connector_id, connector_user_id)</c>
/// — one external identity maps to at most one human per tenant. Including
/// <see cref="HumanId"/> in the uniqueness was deliberately rejected during
/// design (#2408 comment thread): allowing two humans to claim the same
/// login would defeat the resolver's "given a login, who is this?" query.
/// </para>
/// <para>
/// Soft delete is intentionally not modelled. Removing an identity tombstones
/// the row outright so the unique-index slot is freed; if a row needs to come
/// back, the operator re-runs <c>spring human identity set</c>.
/// </para>
/// </remarks>
public class HumanConnectorIdentityEntity : ITenantScopedEntity
{
    /// <summary>Surrogate primary key.</summary>
    public Guid Id { get; set; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>
    /// The stable human UUID this identity maps to. References
    /// <c>humans.id</c>; deletion of the human is not cascaded — operators
    /// must delete identities explicitly before deleting a human (rare
    /// today; OSS does not yet support human deletion).
    /// </summary>
    public Guid HumanId { get; set; }

    /// <summary>
    /// The connector slug (e.g. <c>github</c>). Matches
    /// <see cref="Cvoya.Spring.Connectors.IConnectorType.Slug"/>; stored as
    /// a free-form string so the schema supports connectors that aren't
    /// yet installed.
    /// </summary>
    public string ConnectorId { get; set; } = string.Empty;

    /// <summary>
    /// The stable external user identifier — for GitHub this is the login
    /// string. A future hardening pass may add a numeric-id companion
    /// column to survive renames, but the login is what callers (the agent's
    /// <c>gh</c> CLI, the PR-review skill) already pass on the wire.
    /// </summary>
    public string ConnectorUserId { get; set; } = string.Empty;

    /// <summary>
    /// Optional human-readable label. Not used for resolution; surfaced in
    /// <c>spring human identity list</c> output so an operator can verify
    /// the mapping at a glance.
    /// </summary>
    public string? DisplayHandle { get; set; }

    /// <summary>UTC timestamp when the row was first inserted.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC timestamp of the most recent update.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
