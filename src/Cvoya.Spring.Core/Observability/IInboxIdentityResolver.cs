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
/// <b>Default implementation</b> (<c>Cvoya.Spring.Dapr.Observability.InboxIdentityResolver</c>):
/// walks the FK on <c>humans.tenant_user_id</c> (ADR-0062 § 1) and returns
/// every Human bound to the calling <c>TenantUser</c>. OSS and cloud
/// share the same query — the OSS-only resolver is gone (ADR-0062 § 7)
/// because the FK collapses the two deployments to one rule. Cloud
/// overlays can still decorate via <c>TryAddScoped</c> if they want to
/// layer audit / cross-tenant guards on top.
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
