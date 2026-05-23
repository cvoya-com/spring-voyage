// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System.IO;

using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Skills;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// On-disk verification that the in-tree
/// <c>packages/conversational-defaults/</c> bundle (ADR-0056 Wave 2 /
/// #2657) ships the contract every consumer depends on:
/// <list type="bullet">
///   <item>the fundamental-core tool grants (ADR-0056 §8),</item>
///   <item>the <c>[PLATFORM CONTRACT — NON-NEGOTIABLE]</c> header
///   verbatim, and</item>
///   <item>the inline pointer to <c>sv.tools.list(&lt;category&gt;)</c>
///   for everything else.</item>
/// </list>
/// The test pins the on-disk shape rather than re-deriving it from a
/// constant so a refactor that accidentally drops the header or one of
/// the grants breaks here instead of silently flowing into every
/// agent's prompt budget.
/// </summary>
public class ConversationalDefaultsBundleManifestTests
{
    [Fact]
    public async Task ResolveAsync_LoadsBundleFromInTreePackage()
    {
        var resolver = BuildResolverFromRepoPackages();

        var bundle = await resolver.ResolveAsync(
            DefaultAgentSkillBundles.ConversationalDefaults,
            TestContext.Current.CancellationToken);

        bundle.PackageName.ShouldBe("spring-voyage/conversational-defaults");
        bundle.SkillName.ShouldBe("conversational-defaults");
    }

    [Fact]
    public async Task Bundle_GrantsExactFundamentalCoreToolSet()
    {
        var resolver = BuildResolverFromRepoPackages();

        var bundle = await resolver.ResolveAsync(
            DefaultAgentSkillBundles.ConversationalDefaults,
            TestContext.Current.CancellationToken);

        // ADR-0056 §8 fundamental-core list — every name and nothing
        // extra. This is the load-bearing contract: a future change
        // that adds or drops a tool here must update the ADR first.
        var expected = new[]
        {
            "sv.messaging.send",
            "sv.messaging.multicast",
            "sv.directory.list",
            "sv.directory.lookup",
            "sv.progress.report",
            "sv.tools.list_categories",
            "sv.tools.list",
        };

        var actual = bundle.RequiredTools.Select(t => t.Name).ToArray();
        actual.ShouldBe(expected, ignoreOrder: true);

        // None of the core grants are advertised as optional —
        // missing any of them would silently degrade the runtime.
        bundle.RequiredTools.ShouldAllBe(t => !t.Optional);
    }

    [Fact]
    public async Task Bundle_PromptPointsAtPlatformContractInLayer1()
    {
        var resolver = BuildResolverFromRepoPackages();

        var bundle = await resolver.ResolveAsync(
            DefaultAgentSkillBundles.ConversationalDefaults,
            TestContext.Current.CancellationToken);

        // The `[PLATFORM CONTRACT — NON-NEGOTIABLE]` header is now
        // emitted once, in Layer 1 (PlatformPromptProvider). The
        // bundle no longer carries a parallel copy; it points the
        // runtime at the platform-layer instructions at the top of
        // the assembled prompt and then enumerates the tools it
        // grants.
        bundle.Prompt.ShouldNotContain("[PLATFORM CONTRACT — NON-NEGOTIABLE]");
        bundle.Prompt.ShouldContain("platform's response contract");
        bundle.Prompt.ShouldContain("platform-layer instructions");
    }

    [Fact]
    public async Task Bundle_PromptNamesFundamentalCoreToolsInline()
    {
        var resolver = BuildResolverFromRepoPackages();

        var bundle = await resolver.ResolveAsync(
            DefaultAgentSkillBundles.ConversationalDefaults,
            TestContext.Current.CancellationToken);

        // Inline naming is load-bearing per ADR-0056 §8 — the runtime
        // sees the core surface in the prompt rather than having to
        // call sv.tools.list_categories before it can reply.
        bundle.Prompt.ShouldContain("sv.messaging.send");
        bundle.Prompt.ShouldContain("sv.messaging.multicast");
        bundle.Prompt.ShouldContain("sv.directory.list");
        bundle.Prompt.ShouldContain("sv.directory.lookup");
        bundle.Prompt.ShouldContain("sv.progress.report");
        bundle.Prompt.ShouldContain("sv.tools.list_categories");
        bundle.Prompt.ShouldContain("sv.tools.list");
    }

    [Fact]
    public async Task Bundle_PromptPointsAtDiscoveryToolForAdditionalCategories()
    {
        var resolver = BuildResolverFromRepoPackages();

        var bundle = await resolver.ResolveAsync(
            DefaultAgentSkillBundles.ConversationalDefaults,
            TestContext.Current.CancellationToken);

        // The discovery-tool pointer is what makes the
        // category-on-demand model work — without it the runtime
        // has no instruction to call sv.tools.list when it needs a
        // tool outside the fundamental core.
        bundle.Prompt.ShouldContain("sv.tools.list(");
    }

    [Fact]
    public async Task Bundle_PromptSurfacesMemoryCategoryByName()
    {
        var resolver = BuildResolverFromRepoPackages();

        var bundle = await resolver.ResolveAsync(
            DefaultAgentSkillBundles.ConversationalDefaults,
            TestContext.Current.CancellationToken);

        // ADR-0056 §8 "Memory" candidate ruling: the bundle grants
        // the memory category visibly so the runtime can pull its
        // tools on demand. The prompt names it so the runtime knows
        // a `memory` category exists without first calling
        // sv.tools.list_categories.
        bundle.Prompt.ShouldContain("memory");
    }

    /// <summary>
    /// Resolves the repo root (AGENTS.md marker) and points the file-
    /// system bundle resolver at its <c>packages/</c> tree. Mirrors the
    /// repo-root lookup other integration tests use.
    /// </summary>
    private static FileSystemSkillBundleResolver BuildResolverFromRepoPackages()
    {
        var repoRoot = ResolveRepoRoot();
        var packagesRoot = Path.Combine(repoRoot, "packages");
        Directory.Exists(packagesRoot).ShouldBeTrue(
            $"packages/ directory must exist under the repo root '{repoRoot}'.");

        var options = new SkillBundleOptions { PackagesRoot = packagesRoot };
        return new FileSystemSkillBundleResolver(
            options,
            NullLogger<FileSystemSkillBundleResolver>.Instance);
    }

    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not resolve repository root from AppContext.BaseDirectory.");
    }
}
