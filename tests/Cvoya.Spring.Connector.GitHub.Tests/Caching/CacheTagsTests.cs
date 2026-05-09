// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.Caching;

using Cvoya.Spring.Connector.GitHub.Caching;

using Shouldly;

using Xunit;

/// <summary>
/// Guards the tag-string contract that both read-side (skills) and
/// invalidation-side (webhook handler) rely on. A change to the canonical
/// shape here is a breaking change to the invalidation protocol.
/// </summary>
public class CacheTagsTests
{
    [Fact]
    public void Repository_Lowercases()
    {
        CacheTags.Repository("Cvoya", "Spring-Voyage").ShouldBe("repo:cvoya/spring-voyage");
    }

    [Fact]
    public void PullRequest_IncludesNumber()
    {
        CacheTags.PullRequest("cvoya", "spring-voyage", 42).ShouldBe("pr:cvoya/spring-voyage#42");
    }

    [Fact]
    public void Issue_IncludesNumber()
    {
        CacheTags.Issue("cvoya", "spring-voyage", 42).ShouldBe("issue:cvoya/spring-voyage#42");
    }

    [Fact]
    public void IssueAndPullRequest_HaveDistinctPrefixes()
    {
        // GitHub treats PR comments and issue comments as the same API surface
        // but the cache intentionally keeps the tags separate so a PR-only
        // event doesn't flush cached issue reads (and vice versa).
        CacheTags.Issue("o", "r", 1).ShouldNotBe(CacheTags.PullRequest("o", "r", 1));
    }

    [Fact]
    public void ProjectV2_IncludesOwnerAndNumber()
    {
        CacheTags.ProjectV2("Acme", 7).ShouldBe("project-v2:acme/7");
    }

    [Fact]
    public void ProjectV2List_IsOwnerScoped()
    {
        CacheTags.ProjectV2List("Acme").ShouldBe("projects-v2-list:acme");
    }

    [Fact]
    public void ProjectV2Item_UsesNodeIdVerbatim()
    {
        // Item GraphQL node ids are already case-sensitive opaque strings
        // (e.g. "PVTI_lAD...") — normalizing would collide separate items.
        CacheTags.ProjectV2Item("PVTI_lADOA").ShouldBe("project-v2-item:PVTI_lADOA");
    }

    [Fact]
    public void ProjectV2_ListAndGet_AreDistinct()
    {
        // List vs get tag disjointness: invalidating a specific item tag
        // must not invalidate the list tag (and vice versa), which the
        // webhook handler relies on to avoid over-flushing.
        CacheTags.ProjectV2("o", 1).ShouldNotBe(CacheTags.ProjectV2List("o"));
        CacheTags.ProjectV2Item("PVTI_1").ShouldNotBe(CacheTags.ProjectV2("o", 1));
        CacheTags.ProjectV2Item("PVTI_1").ShouldNotBe(CacheTags.ProjectV2List("o"));
    }
}
