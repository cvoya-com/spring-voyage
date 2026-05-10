// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using System.Text.Json;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Auth;

/// <summary>
/// Persists one row of operator-editable runtime config for a single unit.
/// 1:1 with <see cref="UnitDefinitionEntity"/>: the row is keyed by the
/// unit's stable Guid and holds the live mutable values that used to be
/// kept in actor state under <c>Unit:Model</c>, <c>Unit:Color</c>,
/// <c>Unit:Provider</c>, <c>Unit:Hosting</c>, <c>Unit:Boundary</c>, and
/// <c>Unit:PermissionInheritance</c>.
///
/// <para>
/// Implements ADR-0040 / #2049 for the unit live-config slice. EF is the
/// authoritative store; <c>UnitActor</c> reads through this row on every
/// <c>GetMetadataAsync</c> / <c>GetBoundaryAsync</c> /
/// <c>GetPermissionInheritanceAsync</c> call and writes through it on
/// every set. There is no actor-state warm cache in v0.1 — the activation
/// path reads from EF and the read is instrumented with a timing metric
/// so the v0.2 cache decision is data-driven (ADR-0040 § 3).
/// </para>
///
/// <para>
/// <see cref="PermissionInheritance"/> living on the same row as the
/// other live-config dimensions is a deliberate ADR-0040 choice:
/// <c>PermissionService.ResolveEffectivePermissionAsync</c> can resolve
/// the inheritance flag in the same SQL query that walks the parent
/// chain, dropping the cold-activation actor-proxy hop the v0.1 service
/// used.
/// </para>
/// </summary>
public class UnitLiveConfigEntity : ITenantScopedEntity
{
    /// <summary>
    /// Stable Guid identity of the unit the row describes. Primary key
    /// and FK back to <see cref="UnitDefinitionEntity.Id"/> (1:1).
    /// </summary>
    public Guid UnitId { get; set; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>
    /// Preferred LLM model identifier the unit defaults its members to,
    /// or <c>null</c> when the operator has not pinned one and
    /// inheritance / runtime defaults apply.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Optional UI color hint surfaced through
    /// <see cref="Cvoya.Spring.Core.Units.UnitMetadata.Color"/>.
    /// </summary>
    public string? Color { get; set; }

    /// <summary>
    /// Optional LLM provider identifier (e.g. <c>ollama</c>,
    /// <c>openai</c>) surfaced through
    /// <see cref="Cvoya.Spring.Core.Units.UnitMetadata.Provider"/>. See
    /// #1065. Distinct from container runtime which lives on the unit's
    /// <c>execution:</c> block.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Optional hosting hint (e.g. <c>ephemeral</c>, <c>persistent</c>)
    /// surfaced through
    /// <see cref="Cvoya.Spring.Core.Units.UnitMetadata.Hosting"/>. See
    /// #1065.
    /// </summary>
    public string? Hosting { get; set; }

    /// <summary>
    /// Permission-inheritance flag (<see cref="UnitPermissionInheritance"/>)
    /// consulted by the hierarchy-aware permission resolver to decide
    /// whether ancestor grants flow through this unit. Persisted as the
    /// enum's ordinal so a default of
    /// <see cref="UnitPermissionInheritance.Inherit"/> (0) maps to the
    /// "no isolation" baseline (ADR-0013 / #414).
    /// </summary>
    public UnitPermissionInheritance PermissionInheritance { get; set; } = UnitPermissionInheritance.Inherit;

    /// <summary>
    /// Persisted <see cref="Cvoya.Spring.Core.Capabilities.UnitBoundary"/>
    /// encoded as JSON, or <c>null</c> when no boundary rules apply
    /// (semantically identical to
    /// <see cref="Cvoya.Spring.Core.Capabilities.UnitBoundary.Empty"/>).
    /// Stored as <c>jsonb</c> on PostgreSQL so the boundary record's
    /// shape can grow without DDL changes.
    /// </summary>
    public JsonElement? Boundary { get; set; }

    /// <summary>
    /// Sticky flag set the first time the operator (or the YAML seed
    /// path) writes an own-expertise list for the unit — even an empty
    /// list. Once <c>true</c>, the activation seeder no longer applies
    /// the YAML seed, so runtime edits survive process restart with
    /// "actor-state wins" precedence (ADR-0040 / #488). The flag is the
    /// EF replacement for "is the actor-state expertise key present?"
    /// </summary>
    public bool ExpertiseInitialised { get; set; }

    /// <summary>
    /// UTC timestamp of the last write to this row. Stamped by the
    /// <c>SpringDbContext</c> audit-timestamp pipeline.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
