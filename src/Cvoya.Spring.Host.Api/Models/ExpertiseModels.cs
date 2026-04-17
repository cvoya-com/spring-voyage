// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Wire shape for a single expertise domain.
/// </summary>
/// <param name="Name">Domain name (e.g. <c>python/fastapi</c>).</param>
/// <param name="Description">Free-form description.</param>
/// <param name="Level">
/// Optional proficiency level — one of <c>beginner | intermediate | advanced
/// | expert</c> (case-insensitive). Kept as a string on the wire so Kiota
/// generates a plain property instead of a composed-type wrapper around a
/// nullable enum.
/// </param>
public record ExpertiseDomainDto(
    string Name,
    string Description,
    string? Level);

/// <summary>
/// Request body for setting an entity's expertise. The caller PUTs the full
/// replacement list — there are no partial-merge semantics. An empty list is
/// a legitimate "clear all expertise" state.
/// </summary>
/// <param name="Domains">The replacement expertise set. Non-null.</param>
public record SetExpertiseRequest(IReadOnlyList<ExpertiseDomainDto> Domains);

/// <summary>
/// Response shape for reads that return only the per-entity own expertise
/// (no aggregation). Used by <c>GET /api/v1/agents/{id}/expertise</c> and
/// <c>GET /api/v1/units/{id}/expertise/own</c>.
/// </summary>
/// <param name="Domains">The configured domains.</param>
public record ExpertiseResponse(IReadOnlyList<ExpertiseDomainDto> Domains);

/// <summary>
/// One aggregated entry: a domain, its contributing origin, and the path
/// from the aggregating unit down to the origin. Matches
/// <see cref="ExpertiseEntry"/>.
/// </summary>
/// <param name="Domain">The contributed domain.</param>
/// <param name="Origin">Scheme + path of the contributing agent or sub-unit.</param>
/// <param name="Path">
/// Ordered addresses from the aggregating unit to <paramref name="Origin"/>.
/// </param>
public record AggregatedExpertiseEntryDto(
    ExpertiseDomainDto Domain,
    AddressDto Origin,
    IReadOnlyList<AddressDto> Path);

/// <summary>
/// Response body for <c>GET /api/v1/units/{id}/expertise</c>: the unit's
/// <em>effective</em> expertise — the recursive composition of its own
/// domains and every descendant's expertise.
/// </summary>
/// <param name="Unit">The aggregating unit.</param>
/// <param name="Entries">Every contributed capability.</param>
/// <param name="Depth">Deepest path walked during aggregation.</param>
/// <param name="ComputedAt">UTC timestamp when the snapshot was computed.</param>
public record AggregatedExpertiseResponse(
    AddressDto Unit,
    IReadOnlyList<AggregatedExpertiseEntryDto> Entries,
    int Depth,
    DateTimeOffset ComputedAt);