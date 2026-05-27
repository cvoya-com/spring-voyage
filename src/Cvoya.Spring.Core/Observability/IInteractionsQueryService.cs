// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Observability;

/// <summary>
/// Provides the per-tenant interactions graph that powers the portal's
/// Interactions visualization (issue #2867) and the corresponding CLI
/// surface. Aggregates over the persisted <c>messages</c> table
/// (ADR-0030 / ADR-0040): one node per addressable seen as sender or
/// recipient in the window, one edge per directed <c>(fromId, toId)</c>
/// pair, and a per-bucket timeline histogram of sent counts.
/// </summary>
/// <remarks>
/// <para>
/// Tenant scoping is automatic via the EF query filter on
/// <see cref="Cvoya.Spring.Core.Tenancy.ITenantContext"/> — implementations
/// must NOT re-scope manually and must NEVER use
/// <c>IgnoreQueryFilters()</c>; cross-tenant reads go through
/// <see cref="Cvoya.Spring.Core.Tenancy.ITenantScopeBypass"/>.
/// </para>
/// <para>
/// Per <see href="../../../docs/decisions/archive/0048-event-vs-request-message-semantics.md">ADR-0048</see>
/// the <c>connector</c> scheme is provenance-only — a connector address
/// stamps a synthetic <c>From</c> on translated webhook events but
/// nothing ever routes <em>to</em> a connector. Implementations MUST
/// preserve that invariant: a connector id never appears as
/// <see cref="InteractionsEdge.ToId"/>.
/// </para>
/// </remarks>
public interface IInteractionsQueryService
{
    /// <summary>
    /// Builds an interactions graph snapshot for the supplied filters.
    /// </summary>
    /// <param name="filters">The time window, scoping, bucket, and cap.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The snapshot — nodes, edges, timeline, and optional truncation payload.</returns>
    Task<InteractionsGraph> GetAsync(
        InteractionsQueryFilters filters,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a per-message history slice for the rewind / scrub mode in
    /// the portal's Interactions view (#2872). Returns the same nodes /
    /// edges as <see cref="GetAsync"/> for the window, plus one
    /// <see cref="InteractionsPulse"/> per Domain message so the portal
    /// can animate the activity message by message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pulses are sorted ascending by timestamp; ties are broken by the
    /// canonical no-dash 32-hex message id so the response is deterministic
    /// for two messages that share a wall-clock millisecond.
    /// </para>
    /// <para>
    /// When the window contains more pulses than
    /// <see cref="InteractionsHistoryFilters.MaxPulses"/>, the OLDEST
    /// pulses are dropped — the rewind affordance benefits more from
    /// recent context than from a complete prefix — and the response
    /// carries an <see cref="InteractionsPulseTruncation"/> reporting the
    /// totals. Node-level truncation via
    /// <see cref="InteractionsHistoryFilters.Cap"/> remains independent;
    /// both branches can fire on the same response.
    /// </para>
    /// </remarks>
    /// <param name="filters">The time window, scoping, cap, and pulse budget.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The history slice — nodes, edges, ordered pulses, and an optional
    /// truncation envelope when either truncation branch fired.
    /// </returns>
    Task<InteractionsHistory> GetHistoryAsync(
        InteractionsHistoryFilters filters,
        CancellationToken cancellationToken = default);
}
