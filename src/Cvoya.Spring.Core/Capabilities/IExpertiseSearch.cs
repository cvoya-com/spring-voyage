// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Searches the expertise directory (#542 — companion to #541's
/// <c>ISkillRegistry</c> / <c>IExpertiseSkillCatalog</c>). A planner, CLI
/// operator, or portal user that knows "I need something that refactors
/// Python" but does NOT know the exact slug calls through this interface
/// to resolve the capability description into concrete slugs + owners.
/// </summary>
/// <remarks>
/// <para>
/// The default OSS implementation is lexical — it indexes slug, display
/// name, description, and domain / tag names and ranks by exact match,
/// then text relevance, then aggregated-coverage depth. Step 2 of issue
/// #542 (semantic / embedding search) is out of scope for the first PR;
/// the contract is the same, so the private cloud repo or a future OSS
/// follow-up can swap in an embedding-backed implementation behind this
/// seam without changing any caller.
/// </para>
/// <para>
/// <b>Boundary.</b> Implementations MUST honour the boundary view context
/// carried on the <see cref="ExpertiseSearchQuery"/>: outside-the-unit
/// callers see only projected entries; inside callers see the full scope.
/// Hiding agent-level expertise from external callers is defence in depth —
/// the boundary-filtered aggregator (#413 / #497) normally already applies
/// projection, but the search layer filters again so a mis-configured
/// boundary never leaks through to a search hit.
/// </para>
/// <para>
/// <b>Performance.</b> Issue #542's acceptance bar is &lt;200ms on a
/// tenant with 1000 entries. The OSS in-memory implementation filters and
/// ranks synchronously off the directory service's cached entry list plus
/// per-entity expertise reads (the actor reads are bounded by the tenant's
/// agent + unit count); for much larger tenants the private cloud repo can
/// back this with Postgres full-text search without changing the interface.
/// </para>
/// </remarks>
public interface IExpertiseSearch
{
    /// <summary>
    /// Runs a search query and returns the ranked page.
    /// </summary>
    /// <param name="query">The search query; every field is optional.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The page of hits plus the unbounded total-matches count so callers
    /// can render pagination without a second call.
    /// </returns>
    Task<ExpertiseSearchResult> SearchAsync(
        ExpertiseSearchQuery query,
        CancellationToken cancellationToken = default);
}