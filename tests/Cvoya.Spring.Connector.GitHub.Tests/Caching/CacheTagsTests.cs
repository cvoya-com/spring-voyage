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
    public void ProjectV2_LowercasesOwnerAndIncludesNumber()
    {
        CacheTags.ProjectV2("Cvoya", 7).ShouldBe("project-v2:cvoya/7");
    }

    [Fact]
    public void ProjectV2Item_UsesRawNodeId()
    {
        // Node ids are already globally unique; no normalization applies.
        CacheTags.ProjectV2Item("PVTI_AbC").ShouldBe("project-v2-item:PVTI_AbC");
    }

    [Fact]
    public void ProjectV2_AndItemTags_HaveDistinctPrefixes()
    {
        CacheTags.ProjectV2("o", 1).ShouldNotStartWith("project-v2-item:");
        CacheTags.ProjectV2Item("PVTI_1").ShouldNotStartWith("project-v2:");
    }
}