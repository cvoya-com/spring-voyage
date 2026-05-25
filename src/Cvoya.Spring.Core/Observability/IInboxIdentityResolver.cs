// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Observability;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Resolves the calling <c>TenantUser</c> to the set of
/// <see cref="Cvoya.Spring.Core.Identifiers"/>-keyed <c>HumanEntity</c>
/// identities the inbox query (<see cref="IThreadQueryService.ListInboxAsync"/>)
/// should match the caller against. Per ADR-0047 §7 and #2766 a
/// <c>TenantUser</c> is an authenticated principal scoped to one tenant
/// while a <c>HumanEntity</c> is a package-declared role-slot — the
/// inbox needs to fan into every Human row that maps to the caller so a
/// message addressed to <em>any</em> of them shows up.
/// </summary>
/// <remarks>
/// <para>
/// <b>OSS default</b> (<c>Cvoya.Spring.Dapr.Observability.OssInboxIdentityResolver</c>):
/// the deployment ships with exactly one TenantUser, the operator; every
/// HumanEntity in the tenant maps to that single principal. The resolver
/// returns the full set of <c>humans.id</c> in the current tenant.
/// </para>
/// <para>
/// <b>Cloud overlay</b>: replaces the OSS impl via DI (<c>TryAddScoped</c>)
/// to walk the explicit <c>Human → TenantUser</c> mapping rows ADR-0047 §7
/// defers to v0.2 — returns only the Humans whose mapping row names the
/// calling TenantUser id.
/// </para>
/// <para>
/// <b>Where this seam lives.</b> The "You-badge" check (<c>useCurrentUser</c>
/// → "is this loaded human the caller?") and the inbox-pending badge (in
/// the engagement list) both consult this same resolver. Keeping the
/// mapping question on one DI seam means the cloud-overlay rule lands in
/// exactly one place.
/// </para>
/// </remarks>
public interface IInboxIdentityResolver
{
    /// <summary>
    /// Returns the set of <c>HumanEntity</c> ids the inbox query should
    /// match recipient addresses against for <paramref name="caller"/>.
    /// </summary>
    /// <param name="caller">
    /// The authenticated caller's address. Expected to carry the
    /// <see cref="Address.TenantUserScheme"/> scheme post-#2768; resolvers
    /// MAY accept other schemes if their cloud-overlay model warrants it
    /// (e.g. a human acting on its own behalf).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The HumanEntity ids that map to the caller. An empty collection
    /// means "the caller maps to no Human row" — the inbox query produces
    /// zero rows in that case.
    /// </returns>
    Task<IReadOnlyCollection<Guid>> ResolveHumanIdsAsync(
        Address caller,
        CancellationToken cancellationToken = default);
}
