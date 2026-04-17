// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// One entry in a unit's aggregated expertise directory. Each entry pins a
/// <see cref="ExpertiseDomain"/> to its <see cref="Origin"/> — the agent or
/// sub-unit that contributed the capability. The origin lets peer-lookup
/// callers (#412) tell <em>where</em> a capability came from so they can
/// route work to the leaf, and lets permission checks (#414) decide whether
/// the requester is allowed to traverse into that origin.
/// </summary>
/// <param name="Domain">The contributed domain.</param>
/// <param name="Origin">
/// Address of the contributor. For a leaf agent this is <c>agent://{id}</c>;
/// for a nested sub-unit that itself contributes expertise, this is
/// <c>unit://{id}</c>. Origin is preserved as-seen (not rewritten through the
/// parent) so a consumer can follow the origin chain one hop at a time.
/// </param>
/// <param name="Path">
/// Ordered chain from the aggregating unit down to <see cref="Origin"/>.
/// The first entry is the aggregating unit itself, the last entry is
/// <see cref="Origin"/>. For a direct member the path has two entries
/// (<c>[unit, origin]</c>). Callers that only need the origin ignore the
/// path; callers that care about how deep the capability sits (e.g. the
/// boundary layer in #413) read the path length.
/// </param>
public record ExpertiseEntry(
    ExpertiseDomain Domain,
    Address Origin,
    IReadOnlyList<Address> Path);