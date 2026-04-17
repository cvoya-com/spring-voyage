// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// A unit's <em>effective</em> expertise: the union of its own configured
/// expertise and every descendant's expertise, each annotated with the
/// contributing origin and the path walked to reach it. See #412.
/// </summary>
/// <remarks>
/// The aggregation walk composes recursively through the unit hierarchy all
/// the way down to each leaf agent. Duplicate <c>(domain-name, origin)</c>
/// pairs are collapsed so the same capability from the same contributor never
/// appears twice, even if the contributor is reachable through multiple
/// DAG paths. When two entries agree on domain name + origin but disagree on
/// <see cref="ExpertiseDomain.Level"/>, the stronger level wins (highest
/// enum value) so "closest to the root" does not silently downgrade an
/// <c>expert</c> contribution.
/// <para>
/// The <see cref="Depth"/> field reports the deepest path that was walked.
/// Callers use it for observability and to spot pathological graphs; the
/// traversal itself enforces the depth cap by throwing
/// <see cref="ExpertiseAggregationException"/>.
/// </para>
/// </remarks>
/// <param name="Unit">The unit whose effective expertise is described.</param>
/// <param name="Entries">Every capability contributed by the unit or any descendant.</param>
/// <param name="Depth">Deepest path walked from <paramref name="Unit"/> during aggregation.</param>
/// <param name="ComputedAt">UTC timestamp at which this snapshot was computed.</param>
public record AggregatedExpertise(
    Address Unit,
    IReadOnlyList<ExpertiseEntry> Entries,
    int Depth,
    DateTimeOffset ComputedAt);