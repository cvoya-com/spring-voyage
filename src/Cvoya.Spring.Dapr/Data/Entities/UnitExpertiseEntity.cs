// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persists one own-expertise domain declared (or operator-assigned)
/// for a single unit. Replaces the actor-state <c>Unit:OwnExpertise</c>
/// list with N tenant-scoped rows so own-expertise survives actor
/// restarts and so the expertise aggregator (#412) and skill registry
/// (#359) can read it from relational data on the read path.
///
/// <para>
/// Implements ADR-0040 / #2049 for the unit own-expertise slice.
/// Uniqueness is enforced on <c>(tenant_id, unit_id, name)</c> via a
/// unique index — a domain name appears at most once per unit per
/// tenant. <c>SetOwnExpertiseAsync</c> uses replace-in-full semantics
/// with last-write-wins on <c>name</c> (case-insensitive) so a caller
/// can patch <c>Level</c> or <c>Description</c> by re-listing the same
/// domain.
/// </para>
///
/// <para>
/// "Own" expertise is the slice the unit advertises on its own behalf,
/// independent of its members. The recursive composition over the
/// member graph still happens at read time in
/// <see cref="IExpertiseAggregator"/>; this table only holds the
/// per-unit slice.
/// </para>
/// </summary>
public class UnitExpertiseEntity : ITenantScopedEntity
{
    /// <summary>Synthetic primary key for the row.</summary>
    public Guid Id { get; set; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Stable Guid identity of the unit the expertise attaches to.</summary>
    public Guid UnitId { get; set; }

    /// <summary>
    /// Domain name — e.g. <c>python/fastapi</c>, <c>distributed-systems</c>.
    /// Free-form; the platform does not impose a taxonomy.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional human-readable description of the domain.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Optional proficiency level (<c>beginner | intermediate | advanced |
    /// expert</c>). <c>null</c> when the source did not supply one.
    /// </summary>
    public ExpertiseLevel? Level { get; set; }

    /// <summary>
    /// Optional JSON-schema document describing a typed input contract
    /// for the domain (#359 agents-as-skills). Stored as text so the
    /// entity is agnostic to the schema content; the JSON shape is
    /// enforced in the API layer when the contract is parsed.
    /// </summary>
    public string? InputSchemaJson { get; set; }

    /// <summary>UTC timestamp when the row was first created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
