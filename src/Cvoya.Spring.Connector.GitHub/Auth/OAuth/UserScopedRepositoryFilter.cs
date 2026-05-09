// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

using System.Collections.Generic;
using System.Linq;

using Cvoya.Spring.Connector.GitHub.Auth;

/// <summary>
/// Pure helpers that intersect the GitHub App's installation-visible
/// repository set with the calling user's OAuth-accessible repository set.
/// Lifted out of <see cref="GitHubConnectorType"/> so the intersection
/// rules are unit-testable in isolation — the endpoint layer wires up
/// the IO and delegates the set-math here.
///
/// <para>
/// The rules are intentionally narrow: an entry survives the
/// intersection iff both sides agree on its identity. We compare on the
/// numeric repository id where available because GitHub guarantees that
/// id is stable across renames; we fall back to a case-insensitive
/// match on <c>full_name</c> for completeness, but real callers always
/// have an id (Octokit surfaces it on every endpoint we use).
/// </para>
///
/// <para>
/// This file closes #1663 / #1505 by making the rule the only path
/// that produces the wizard's repository droplist when an OAuth user
/// session is in play. The endpoint layer is fail-closed when no
/// session is supplied — see <see cref="GitHubConnectorType"/> for the
/// 401 + <c>missingOAuth=true</c> contract.
/// </para>
/// </summary>
public static class UserScopedRepositoryFilter
{
    /// <summary>
    /// Intersect the installation-scoped repos with the user-accessible
    /// repos and return the rows that belong on the droplist. Order is
    /// stable: alphabetical by <c>full_name</c>, case-insensitive — the
    /// caller renders this directly into a dropdown so jitter between
    /// renders would be user-visible.
    /// </summary>
    /// <param name="installationRepos">
    /// Repos the App can see in a single installation (as returned by
    /// <c>GET /installation/repositories</c>).
    /// </param>
    /// <param name="userAccessibleRepoIds">
    /// Repository ids the user can see (as returned by
    /// <c>GET /user/repos</c> or
    /// <c>GET /user/installations/{id}/repositories</c>). When
    /// <c>null</c>, no user-side filtering is applied — but in practice
    /// the endpoint never invokes this overload with a null set; passing
    /// null would defeat the purpose of the helper.
    /// </param>
    /// <returns>The intersected, ordered set.</returns>
    public static IReadOnlyList<GitHubInstallationRepository> Intersect(
        IEnumerable<GitHubInstallationRepository> installationRepos,
        IReadOnlySet<long>? userAccessibleRepoIds)
    {
        ArgumentNullException.ThrowIfNull(installationRepos);

        IEnumerable<GitHubInstallationRepository> filtered = installationRepos;
        if (userAccessibleRepoIds is not null)
        {
            filtered = filtered.Where(r => userAccessibleRepoIds.Contains(r.RepositoryId));
        }

        return filtered
            .OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Fallback overload that intersects on <c>full_name</c> rather than
    /// repository id. Kept for symmetry with the issue's example
    /// reproduction (which talked about owner/name pairs); used only in
    /// tests that don't model ids. Production endpoints always have ids
    /// on both sides and call <see cref="Intersect(IEnumerable{GitHubInstallationRepository}, IReadOnlySet{long}?)"/>.
    /// </summary>
    public static IReadOnlyList<GitHubInstallationRepository> IntersectByFullName(
        IEnumerable<GitHubInstallationRepository> installationRepos,
        IReadOnlySet<string>? userAccessibleFullNames)
    {
        ArgumentNullException.ThrowIfNull(installationRepos);

        IEnumerable<GitHubInstallationRepository> filtered = installationRepos;
        if (userAccessibleFullNames is not null)
        {
            // GitHub logins/repos are case-insensitive on lookup but the
            // case-preserving original is what we render. Comparer must
            // match on the user side.
            var lookup = userAccessibleFullNames is HashSet<string> hs
                && hs.Comparer == StringComparer.OrdinalIgnoreCase
                ? userAccessibleFullNames
                : new HashSet<string>(userAccessibleFullNames, StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(r => lookup.Contains(r.FullName));
        }

        return filtered
            .OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
