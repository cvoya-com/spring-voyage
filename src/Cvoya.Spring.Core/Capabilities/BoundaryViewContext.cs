// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Identifies the caller (viewer) on whose behalf an aggregated-expertise
/// read is being performed. Supplied to
/// <see cref="IExpertiseAggregator.GetAsync(Address, BoundaryViewContext, CancellationToken)"/>
/// so boundary rules can decide whether the caller is inside the unit (and
/// should see the raw aggregate) or outside (and should see opacity /
/// projection / synthesis applied).
/// </summary>
/// <remarks>
/// <para>
/// The boundary layer only needs the caller's address here. Richer
/// permission-aware filtering (who has deep-access rights, unit-scoped
/// operator roles) is tracked under <see href="https://github.com/cvoya-com/spring-voyage/issues/414">#414</see>;
/// this context shape is intentionally small so that PR can layer on top
/// without changing the aggregator interface again.
/// </para>
/// <para>
/// A call with <see cref="Internal"/> set to <c>null</c> is treated as an
/// <em>external</em> caller — the most restrictive view. That is the safe
/// default: a caller that forgets to identify itself must not accidentally
/// gain access to opaque entries.
/// </para>
/// </remarks>
/// <param name="Caller">
/// Address of the caller. For service-to-service reads this is the
/// calling unit or agent. <c>null</c> when the reader is unauthenticated
/// / unknown; treated as external.
/// </param>
/// <param name="Internal">
/// <c>true</c> when the caller is the unit itself or a descendant member —
/// i.e. "inside the unit boundary". When <c>true</c>, opacity / projection /
/// synthesis rules are bypassed and the raw aggregator output is returned.
/// Defaults to <c>false</c>.
/// </param>
public record BoundaryViewContext(
    Address? Caller = null,
    bool Internal = false)
{
    /// <summary>
    /// Canonical "external caller" context — no caller identity, outside the
    /// unit boundary. Produces the most-restrictive view. Used by HTTP
    /// endpoints that have no authenticated unit identity, or by tests that
    /// want the boundary-applied shape.
    /// </summary>
    public static BoundaryViewContext External { get; } = new();

    /// <summary>
    /// Canonical "inside the unit" context — bypasses boundary rules. Used
    /// by read paths that are known to originate from the unit itself (or a
    /// descendant), and by <see cref="IExpertiseAggregator.GetAsync(Address, CancellationToken)"/>
    /// which is the legacy "raw" entry point.
    /// </summary>
    public static BoundaryViewContext InsideUnit { get; } = new(Internal: true);
}