// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Canonical Guid sentinels for the OSS deployment's well-known
/// <c>TenantUser</c> rows. Companion to <see cref="OssTenantIds"/>: that
/// type names the well-known tenant; this type names the well-known
/// principals scoped to it.
///
/// <para>
/// The OSS deployment ships with exactly one <c>TenantUser</c> — the
/// operator running the install. Its id is pinned here as a deterministic
/// v5 UUID so the value is immutable across releases and can be
/// reproduced from outside the platform via the same namespace + label.
/// Cloud overlays carry many <c>TenantUser</c> rows per tenant; none of
/// them claim this sentinel.
/// </para>
///
/// <para>
/// <b>Why a separate class.</b> Co-locating the operator's id on
/// <see cref="OssTenantIds"/> was considered for symmetry and rejected —
/// <see cref="OssTenantIds"/> names a kind of well-known id (tenant);
/// this type names a different kind (tenant user). Per ADR-0047 §3 the
/// discrimination is the point: ADR-0036 §1 worked to keep actor kinds
/// distinct at the class level, and this split preserves that.
/// </para>
///
/// <para>
/// <b>Why a v5 UUID.</b> Same rationale as <see cref="OssTenantIds"/>:
/// <see cref="Guid.Empty"/> is reserved for "uninitialised / programmer
/// error", a pattern Guid would claim sentinel space without provenance,
/// and a random v4 would lose the "anyone can recompute it" property
/// that makes pins auditable from outside the platform.
/// </para>
///
/// <para>
/// <b>Derivation.</b>
/// <code>
/// namespace = 00000000-0000-0000-0000-000000000000
/// label     = "cvoya/tenant-user/oss-operator"
/// uuidv5    = 5c4c8e29-d91b-5b50-8651-64536cfb68ee
/// </code>
/// Reproduce with Python:
/// <c>uuid.uuid5(uuid.UUID("00000000-0000-0000-0000-000000000000"), "cvoya/tenant-user/oss-operator")</c>.
/// </para>
/// </summary>
public static class OssTenantUserIds
{
    /// <summary>
    /// Stable identifier for the OSS operator's <c>TenantUser</c> row in
    /// the OSS default tenant. Computed as the deterministic v5 UUID over
    /// namespace <c>00000000-0000-0000-0000-000000000000</c> and label
    /// <c>"cvoya/tenant-user/oss-operator"</c>; pinned here so the value
    /// is immutable across releases and recomputable from outside the
    /// platform.
    /// </summary>
    public static readonly Guid Operator = new("5c4c8e29-d91b-5b50-8651-64536cfb68ee");

    /// <summary>
    /// Dashed string form of <see cref="Operator"/>
    /// (<c>5c4c8e29-d91b-5b50-8651-64536cfb68ee</c>) — exposed as a
    /// literal for grep-ability across configuration files, dashboards,
    /// and audit logs that may render Guids in either form.
    /// </summary>
    public const string OperatorDashed = "5c4c8e29-d91b-5b50-8651-64536cfb68ee";

    /// <summary>
    /// No-dash 32-character form of <see cref="Operator"/>
    /// (<c>5c4c8e29d91b5b50865164536cfb68ee</c>) — the canonical wire
    /// form emitted by every public surface. Exposed as a literal for the
    /// same grep-ability reason as <see cref="OperatorDashed"/>.
    /// </summary>
    public const string OperatorNoDash = "5c4c8e29d91b5b50865164536cfb68ee";
}
