// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

/// <summary>
/// Slack-specific lookup table: <c>team_id ↔ tenant_id</c>. Per
/// ADR-0061 §7.5 the OAuth callback resolves the installing
/// workspace's <c>team_id</c> to an SV tenant id; in OSS the table
/// has one row, in cloud one row per installed workspace. The
/// per-team_id uniqueness invariant is enforced by the unique index.
///
/// <para>
/// The lookup is intentionally cross-tenant — the Slack inbound
/// webhook handler arrives with a <c>team_id</c> and needs to find
/// the SV tenant that owns it. The model snapshot therefore omits
/// the per-tenant query filter so it can run as a global lookup.
/// </para>
///
/// <para>
/// The entity is Slack-specific by name today; the equivalent
/// "external_key → tenant_id" generalisation is a follow-up if more
/// connectors need the same shape (#2456-style App-level delivery
/// resolution for non-Slack workspace connectors).
/// </para>
/// </summary>
public class TenantSlackWorkspaceMapEntity
{
    /// <summary>Synthetic primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The Slack workspace id (e.g. <c>T123456</c>). Unique across
    /// the table — the same workspace cannot map to two SV tenants.
    /// </summary>
    public string TeamId { get; set; } = string.Empty;

    /// <summary>
    /// The SV tenant id this workspace is bound to. Not unique —
    /// future cloud deployments may map several workspaces to the
    /// same tenant if multi-workspace-per-tenant generalises beyond
    /// OSS (ADR-0061 §7.5).
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Optional workspace display name captured at install time.
    /// Pure presentation; never used as an identifier.
    /// </summary>
    public string? TeamName { get; set; }

    /// <summary>UTC timestamp of when the map row was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
