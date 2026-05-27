// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persisted record for a <c>TenantUser</c> — an authenticated principal
/// of Spring Voyage scoped to a single tenant (ADR-0047 §1).
/// </summary>
/// <remarks>
/// <para>
/// Per ADR-0047 §1 every <c>TenantUser</c> is bound to exactly one
/// tenant; the same human authenticated against two tenants produces
/// two distinct rows. Cross-tenant identity sharing is explicitly not
/// modelled — there is no "global user" concept.
/// </para>
/// <para>
/// The natural key is <c>(tenant_id, auth_subject)</c>. <see cref="AuthSubject"/>
/// is nullable so the OSS operator row — pinned by the deterministic
/// <see cref="OssTenantUserIds.Operator"/> UUID — can exist without an
/// OAuth subject in dev installs where no IdP is configured. Cloud
/// deployments populate <see cref="AuthSubject"/> with the OAuth
/// <c>sub</c> claim on first sign-in.
/// </para>
/// <para>
/// Display-side connector identities (GitHub login, Slack handle) live on
/// <see cref="TenantUserConnectorIdentityEntity"/> rows keyed on
/// <c>(tenant, tenant_user, connector)</c>; auth-side credentials (PATs,
/// App installations) live on the unit binding row per ADR-0047 §§ 5–6
/// and are unrelated to this entity.
/// </para>
/// </remarks>
public class TenantUserEntity : ITenantScopedEntity
{
    /// <summary>Stable Guid primary key.</summary>
    public Guid Id { get; set; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>
    /// OAuth <c>sub</c> claim that authenticates this tenant user, or
    /// <c>null</c> for OSS dev installs where the operator does not OAuth-
    /// authenticate (the row is pinned by <see cref="OssTenantUserIds.Operator"/>
    /// in that case). Cloud deployments populate this on first sign-in.
    /// </summary>
    public string? AuthSubject { get; set; }

    /// <summary>
    /// Human-readable display name. Per ADR-0036 §1 must be non-empty and
    /// must not parse as a Guid; validation runs on the API surface
    /// (<c>DisplayNameProblems.ValidateOrProblem</c>) at write time.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Optional single-line description, parallel to the editable
    /// description on <see cref="HumanEntity"/> / agent / unit. Null when
    /// no description has been set; the storage column is nullable.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the optional "primary" Human binding for this
    /// <c>TenantUser</c> (ADR-0062 § 2). Pins which of the user's bound
    /// <c>Human</c> rows is the default sender ("hat") for new outbound
    /// messages (composer-launched sends and CLI <c>spring message send</c>
    /// without an explicit <c>--as</c>). Within an existing thread the
    /// reply composer pins the hat the thread came in on regardless of
    /// this value.
    ///
    /// <para>
    /// The column is nullable to allow a freshly seeded <c>TenantUser</c>
    /// with no Humans yet, and is set automatically when the user's first
    /// Human binding is created. The operator can repin via the portal
    /// identity settings or the CLI.
    /// </para>
    /// </summary>
    public Guid? PrimaryHumanId { get; set; }

    /// <summary>UTC timestamp when the row was first inserted.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC timestamp of the most recent update.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
