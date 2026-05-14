// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.OAuth;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Auth.OAuth;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="UserScopedRepositoryFilter"/> — the pure
/// helper that intersects the App's installation-visible repos with the
/// user's OAuth-accessible repos. Lifted out of the endpoint so the
/// set-math is testable without spinning up the host (#1663).
/// </summary>
public class UserScopedRepositoryFilterTests
{
    [Fact]
    public void Intersect_FiltersToUserAccessibleIds()
    {
        // The canonical example from #1663:
        //   App installations:  [a, b, c, d]
        //   User-accessible:    [b, c, e]
        //   Expected:           [b, c]
        var installationRepos = new[]
        {
            Repo(1, "a"),
            Repo(2, "b"),
            Repo(3, "c"),
            Repo(4, "d"),
        };
        var userIds = new HashSet<long> { 2, 3, 5 };

        var result = UserScopedRepositoryFilter.Intersect(installationRepos, userIds);

        result.Select(r => r.FullName).ShouldBe(new[] { "owner/b", "owner/c" });
    }

    [Fact]
    public void Intersect_NullUserSet_ReturnsAllOrdered()
    {
        // Defensive default: passing null should leave the input untouched
        // beyond the alphabetical sort. Production endpoints never invoke
        // this overload with null (they short-circuit to the missing-OAuth
        // 401 path first), but the helper still has to behave deterministically.
        var installationRepos = new[]
        {
            Repo(1, "zebra"),
            Repo(2, "alpha"),
            Repo(3, "mango"),
        };

        var result = UserScopedRepositoryFilter.Intersect(installationRepos, userAccessibleRepoIds: null);

        result.Select(r => r.FullName).ShouldBe(
            new[] { "owner/alpha", "owner/mango", "owner/zebra" });
    }

    [Fact]
    public void Intersect_EmptyUserSet_ReturnsEmpty()
    {
        var installationRepos = new[]
        {
            Repo(1, "platform"),
            Repo(2, "ui"),
        };
        var userIds = new HashSet<long>();

        var result = UserScopedRepositoryFilter.Intersect(installationRepos, userIds);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Intersect_OutputIsAlphabeticalCaseInsensitive()
    {
        // GitHub returns repos in install-time order, which would jitter
        // the dropdown between renders. The helper sorts case-
        // insensitively to keep "Foo" and "foo" together regardless of
        // stored casing.
        var installationRepos = new[]
        {
            Repo(1, "Zoo"),
            Repo(2, "apple"),
            Repo(3, "Banana"),
            Repo(4, "alpha"),
        };
        var userIds = new HashSet<long> { 1, 2, 3, 4 };

        var result = UserScopedRepositoryFilter.Intersect(installationRepos, userIds);

        result.Select(r => r.FullName).ShouldBe(new[]
        {
            "owner/alpha",
            "owner/apple",
            "owner/Banana",
            "owner/Zoo",
        });
    }

    [Fact]
    public void IntersectByFullName_FiltersOnFullNameCaseInsensitive()
    {
        var installationRepos = new[]
        {
            Repo(1, "Platform"),
            Repo(2, "ui"),
            Repo(3, "infra"),
        };
        // Mixed casing on the user side — must still match "Platform".
        var userFullNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "owner/platform",
            "owner/ui",
        };

        var result = UserScopedRepositoryFilter.IntersectByFullName(installationRepos, userFullNames);

        result.Select(r => r.FullName).ShouldBe(new[] { "owner/Platform", "owner/ui" });
    }

    [Fact]
    public void IntersectByFullName_AcceptsCaseSensitiveSetAndStillMatchesCaseInsensitively()
    {
        // Even when the caller passes a case-sensitive set, the helper
        // wraps it in a case-insensitive lookup. GitHub's lookup
        // semantics are case-insensitive and the helper must match that.
        var installationRepos = new[]
        {
            Repo(1, "Platform"),
        };
        var userFullNames = new HashSet<string> { "owner/platform" };

        var result = UserScopedRepositoryFilter.IntersectByFullName(installationRepos, userFullNames);

        result.Single().FullName.ShouldBe("owner/Platform");
    }

    [Fact]
    public void Intersect_NullInstallationRepos_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            UserScopedRepositoryFilter.Intersect(installationRepos: null!, userAccessibleRepoIds: null));
    }

    private static GitHubInstallationRepository Repo(long id, string name) =>
        new(id, "owner", name, $"owner/{name}", Private: false);
}
