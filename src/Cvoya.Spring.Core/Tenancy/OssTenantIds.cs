// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Canonical Guid sentinels for the OSS deployment.
///
/// <para>
/// The OSS platform ships functionally single-tenant. Every tenant-scoped
/// row in a fresh OSS install is owned by <see cref="Default"/>, the
/// deterministic v5 UUID derived once and pinned as a literal here so the
/// value never drifts between runtime computation and stored data.
/// </para>
///
/// <para>
/// <b>Why a v5 UUID.</b> <see cref="Guid.Empty"/> is reserved for
/// "uninitialized / programmer error" — it must never be reused as a real
/// sentinel. A pattern Guid (e.g. <c>...0000000001</c>) would claim a
/// chunk of low-numbered Guid space for a single decision and provides no
/// provenance. A v5 UUID over a fixed namespace + label is recomputable,
/// self-documenting, and collision-free against random-Guid generation.
/// </para>
///
/// <para>
/// <b>Derivation.</b>
/// <code>
/// namespace = 00000000-0000-0000-0000-000000000000
/// label     = "cvoya/tenant/oss-default"
/// uuidv5    = dd55c4ea-8d72-5e43-a9df-88d07af02b69
/// </code>
/// The constant is pinned, not recomputed, so a private cloud override can
/// reproduce it via the same namespace + label without depending on the
/// computation library at runtime.
/// </para>
/// </summary>
public static class OssTenantIds
{
    /// <summary>
    /// Stable identifier for the OSS default tenant. Computed as the
    /// deterministic v5 UUID over namespace
    /// <c>00000000-0000-0000-0000-000000000000</c> and label
    /// <c>"cvoya/tenant/oss-default"</c>; pinned here so the value is
    /// immutable across releases and recomputable from outside the platform.
    /// </summary>
    public static readonly Guid Default = new("dd55c4ea-8d72-5e43-a9df-88d07af02b69");

    /// <summary>
    /// Dashed string form of <see cref="Default"/>
    /// (<c>dd55c4ea-8d72-5e43-a9df-88d07af02b69</c>) — exposed as a literal
    /// for grep-ability across configuration files, dashboards, and audit
    /// logs that may render Guids in either form.
    /// </summary>
    public const string DefaultDashed = "dd55c4ea-8d72-5e43-a9df-88d07af02b69";

    /// <summary>
    /// No-dash 32-character form of <see cref="Default"/>
    /// (<c>dd55c4ea8d725e43a9df88d07af02b69</c>) — the canonical wire form
    /// emitted by every public surface. Exposed as a literal for the same
    /// grep-ability reason as <see cref="DefaultDashed"/>.
    /// </summary>
    public const string DefaultNoDash = "dd55c4ea8d725e43a9df88d07af02b69";
}
