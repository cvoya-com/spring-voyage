// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Structured query submitted to <see cref="IExpertiseSearch"/> (#542). Pairs
/// free-text search with per-field filters so callers can narrow the result
/// set without post-filtering client-side. Every field is optional — an
/// empty query returns the full directory (subject to boundary).
/// </summary>
/// <remarks>
/// <para>
/// This record is the OSS search contract. The default implementation is a
/// lexical / full-text search (ranks by exact slug/tag/origin match, then
/// text relevance, then aggregated-coverage) per issue #542 Step 1. A
/// separate implementation can layer semantic / embedding search (#542
/// Step 2) without changing this query shape.
/// </para>
/// <para>
/// <see cref="Caller"/> and <see cref="Context"/> drive boundary scoping:
/// outside a unit, only projected entries are visible; inside, the caller
/// sees the full scope. Boundary rules are enforced by the implementation —
/// callers cannot bypass them by passing the "inside" context unless they
/// really are inside the unit.
/// </para>
/// </remarks>
/// <param name="Text">
/// Free-text query matched against slug, display name, description, tag /
/// domain name, and aggregated-coverage path. Empty / null matches every
/// entry (subject to filters and boundary).
/// </param>
/// <param name="Owner">
/// Optional owner filter — restrict to entries contributed by this address.
/// Matches on <see cref="ExpertiseEntry.Origin"/> exact equality; for unit
/// addresses this covers both own expertise and descendant contributions
/// reachable through that unit.
/// </param>
/// <param name="Domains">
/// Optional set of domain / tag names. When non-empty, an entry matches
/// when its <see cref="ExpertiseDomain.Name"/> exactly equals any supplied
/// value (case-insensitive). Useful for filtering by tag buckets the caller
/// already knows about.
/// </param>
/// <param name="TypedOnly">
/// When <c>true</c>, only entries with a non-null
/// <see cref="ExpertiseDomain.InputSchemaJson"/> (i.e. skill-callable
/// typed-contract entries) surface. When <c>false</c> (default),
/// consultative-only entries are included.
/// </param>
/// <param name="Caller">
/// Optional caller address for boundary scoping. See
/// <see cref="BoundaryViewContext.Caller"/>.
/// </param>
/// <param name="Context">
/// Boundary view context. Defaults to <see cref="BoundaryViewContext.External"/>
/// — the safest view. Callers that want the inside-the-unit scope must
/// pass <see cref="BoundaryViewContext.InsideUnit"/> (or a caller-aware
/// context).
/// </param>
/// <param name="Limit">
/// Maximum results to return. Defaults to <c>50</c>. The implementation
/// caps this at <see cref="MaxLimit"/> to protect the catalog.
/// </param>
/// <param name="Offset">
/// Number of results to skip before applying <see cref="Limit"/>. Used for
/// pagination. Defaults to <c>0</c>.
/// </param>
public record ExpertiseSearchQuery(
    string? Text = null,
    Address? Owner = null,
    IReadOnlyList<string>? Domains = null,
    bool TypedOnly = false,
    Address? Caller = null,
    BoundaryViewContext? Context = null,
    int Limit = 50,
    int Offset = 0)
{
    /// <summary>Default page size when <see cref="Limit"/> is not supplied.</summary>
    public const int DefaultLimit = 50;

    /// <summary>
    /// Hard cap on <see cref="Limit"/>. The implementation clamps larger
    /// values down so a mis-configured caller cannot ask for the entire
    /// catalog in one shot (and a <c>1000</c>-entry tenant is still cheap
    /// under this bound).
    /// </summary>
    public const int MaxLimit = 200;
}