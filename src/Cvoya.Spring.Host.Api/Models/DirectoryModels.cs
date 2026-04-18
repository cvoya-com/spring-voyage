// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Response body representing a directory entry.
/// </summary>
/// <param name="Address">The address of the registered component.</param>
/// <param name="ActorId">The actor identifier.</param>
/// <param name="DisplayName">The human-readable display name.</param>
/// <param name="Description">A description of the component.</param>
/// <param name="Role">The role identifier, if any.</param>
/// <param name="RegisteredAt">The timestamp when the component was registered.</param>
public record DirectoryEntryResponse(
    AddressDto Address,
    string ActorId,
    string DisplayName,
    string Description,
    string? Role,
    DateTimeOffset RegisteredAt);

/// <summary>
/// Request body for <c>POST /api/v1/directory/search</c> (#542). Every
/// field is optional. An empty body returns the full directory (subject to
/// the caller's boundary context) — useful for "give me the catalog" flows
/// that don't have a text query yet.
/// </summary>
/// <param name="Text">Free-text query. Case-insensitive; empty matches everything.</param>
/// <param name="Owner">Optional owner filter as a typed address DTO.</param>
/// <param name="Domains">Optional list of domain names or slugs to filter on.</param>
/// <param name="TypedOnly">When <c>true</c>, only typed-contract (skill-callable) entries surface.</param>
/// <param name="InsideUnit">
/// When <c>true</c>, the caller is signalling they are inside a unit
/// boundary and wants the full scope. Defaults to <c>false</c> — the
/// safest (most-restrictive) view. Callers must authenticate as the unit
/// itself (or a descendant member) for this to matter in downstream checks;
/// the search layer itself does not re-validate the claim here because the
/// boundary decorator on the aggregator already applies opacity from the
/// resolved caller identity in <see cref="Caller"/>.
/// </param>
/// <param name="Caller">
/// Optional caller address for boundary scoping. When null, treated as an
/// external caller.
/// </param>
/// <param name="Limit">Page size (defaults to 50, capped at 200).</param>
/// <param name="Offset">Pagination offset (defaults to 0).</param>
public record DirectorySearchRequest(
    string? Text = null,
    AddressDto? Owner = null,
    IReadOnlyList<string>? Domains = null,
    bool TypedOnly = false,
    bool InsideUnit = false,
    AddressDto? Caller = null,
    int Limit = 50,
    int Offset = 0);

/// <summary>
/// One hit in a <c>POST /api/v1/directory/search</c> response (#542).
/// </summary>
/// <param name="Slug">Directory-addressable slug.</param>
/// <param name="Domain">The matched expertise domain.</param>
/// <param name="Owner">Origin address (agent or unit).</param>
/// <param name="OwnerDisplayName">Display name of the owning component.</param>
/// <param name="AggregatingUnit">
/// Set when the hit surfaced via a descendant unit's projection; null for
/// direct hits.
/// </param>
/// <param name="TypedContract"><c>true</c> when the domain advertises an input schema.</param>
/// <param name="Score">Ranking score — higher is better.</param>
/// <param name="MatchReason">Short human-readable explanation of the match.</param>
public record DirectorySearchHitResponse(
    string Slug,
    ExpertiseDomainDto Domain,
    AddressDto Owner,
    string OwnerDisplayName,
    AddressDto? AggregatingUnit,
    bool TypedContract,
    double Score,
    string MatchReason);

/// <summary>
/// Response body for <c>POST /api/v1/directory/search</c> (#542).
/// </summary>
/// <param name="Hits">The ranked page.</param>
/// <param name="TotalCount">Total matches before pagination.</param>
/// <param name="Limit">Effective page size applied.</param>
/// <param name="Offset">Offset applied.</param>
public record DirectorySearchResponse(
    IReadOnlyList<DirectorySearchHitResponse> Hits,
    int TotalCount,
    int Limit,
    int Offset);