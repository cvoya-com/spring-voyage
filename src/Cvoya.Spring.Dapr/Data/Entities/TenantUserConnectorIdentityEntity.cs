// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persisted mapping row that binds a <c>TenantUser</c> UUID to a
/// connector-native display identity (e.g. a GitHub login) per
/// ADR-0047 §2. Replaces the v0.1-internal <c>HumanConnectorIdentity</c>
/// shape rekeyed off the <c>Human</c> row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Display-side only.</b> No PAT, no installation override, no auth
/// fields, no <c>config_json</c>. The row's single job is answering
/// "who is this SV tenant user in connector X terms?" for
/// <c>@</c>-mention rendering, <c>--add-reviewer</c> calls, and audit
/// attribution. Outbound credentials live on the unit binding row per
/// ADR-0047 §§ 5–6.
/// </para>
/// <para>
/// <b>Indices.</b> Two unique constraints per ADR-0047 §2:
/// <list type="number">
///   <item><description>
///     <c>(tenant_id, tenant_user_id, connector_id)</c> — the natural key.
///     One row per <c>(tenant_user, connector)</c> pair. Re-running
///     "set my GitHub username" upserts in place rather than creating a
///     second row.
///   </description></item>
///   <item><description>
///     <c>(tenant_id, connector_id, username)</c> — the reverse lookup
///     "who is GitHub login <c>X</c> in tenant T?" Backed by the
///     <see cref="Cvoya.Spring.Core.Security.ITenantUserConnectorIdentityResolver.ResolveTenantUserByUsernameAsync"/>
///     contract.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// Soft delete is not modelled. Removing an identity hard-deletes the row
/// so both unique-index slots free up immediately; the operator re-runs
/// <c>spring user identity set</c> (Phase G CLI rename) to restore.
/// </para>
/// </remarks>
public class TenantUserConnectorIdentityEntity : ITenantScopedEntity
{
    /// <summary>Surrogate primary key.</summary>
    public Guid Id { get; set; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>
    /// The <see cref="TenantUserEntity.Id"/> this identity belongs to.
    /// Cascade behaviour: deletion of the tenant user is not modelled —
    /// the OSS operator row is permanent, and cloud-side tenant-user
    /// lifecycle is owned by a separate ADR.
    /// </summary>
    public Guid TenantUserId { get; set; }

    /// <summary>
    /// The connector slug (e.g. <c>github</c>). Matches
    /// <see cref="Cvoya.Spring.Connectors.IConnectorType.Slug"/>; stored
    /// as a free-form string so the schema supports connectors that
    /// aren't yet installed.
    /// </summary>
    public string ConnectorId { get; set; } = string.Empty;

    /// <summary>
    /// The connector-side login (e.g. GitHub <c>octocat</c>, Slack
    /// <c>alice</c>). No leading <c>@</c>. Resolution writes normalise to
    /// lower-trim form; reads compare with the platform's default
    /// case-sensitive equality.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Optional human-friendly rendering (e.g. <c>"Alice Smith (@alice)"</c>).
    /// Falls back to <see cref="Username"/> when <c>null</c>; surfaced
    /// in identity-list output so an operator can verify the mapping at
    /// a glance.
    /// </summary>
    public string? DisplayHandle { get; set; }

    /// <summary>UTC timestamp when the row was first inserted.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC timestamp of the most recent update.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
