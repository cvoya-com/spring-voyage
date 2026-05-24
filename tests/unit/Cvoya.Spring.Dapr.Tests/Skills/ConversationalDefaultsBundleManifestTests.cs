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
/// #2657; reshaped by #2670) holds to its post-#2670 contract:
/// <list type="bullet">
///   <item>does not duplicate the platform-generated tooling
///   instructions (the always-on platform-tool catalog now lives in
///   Layer 1 — the bundle must not re-name those tools),</item>
///   <item>still points the runtime at the platform-layer contract
///   for the response model, and</item>
///   <item>still surfaces the package-specific <c>memory</c> grant
///   alongside the discovery pointer for it.</item>
/// </list>
/// The test pins the on-disk shape rather than re-deriving it from a
/// constant so a refactor that accidentally re-adds the duplication or
/// drops the package-specific guidance breaks here instead of silently
/// flowing into every agent's prompt budget.
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

    /// <summary>
    /// #2670: the bundle no longer re-grants the fundamental-core
    /// tools. <c>ToolGrantResolver.EnumeratePlatformTools</c> grants
    /// every <c>sv.*</c> registry tool with
    /// <see cref="ToolProvenance.Platform"/>; surfacing the same names
    /// through the bundle's <c>RequiredTools</c> list would double up
    /// the rendered Required-tools sub-section and duplicate the
    /// Layer 1 catalog.
    /// </summary>
    [Fact]
    public async Task Bundle_RequiredToolsListIsEmpty_AfterPlatformCatalogMovedToLayer1()
    {
        var resolver = BuildResolverFromRepoPackages();

        var bundle = await resolver.ResolveAsync(
            DefaultAgentSkillBundles.ConversationalDefaults,
            TestContext.Current.CancellationToken);

        bundle.RequiredTools.ShouldBeEmpty(
            "The conversational-defaults bundle is prompt-only after #2670; the platform-tool catalog lives in Layer 1.");
    }

    [Fact]
    public async Task Bundle_PromptPointsAtPlatformContractInLayer1()
    {
        var resolver = BuildResolverFromRepoPackages();

        var bundle = await resolver.ResolveAsync(
            DefaultAgentSkillBundles.ConversationalDefaults,
            TestContext.Current.CancellationToken);

        // The `[PLATFORM CONTRACT — NON-NEGOTIABLE]` header is
        // emitted once, in Layer 1 (PlatformPromptProvider). The
        // bundle must not carry a parallel copy; it points the
        // runtime at the platform-layer instructions at the top of
        // the assembled prompt.
        bundle.Prompt.ShouldNotContain("[PLATFORM CONTRACT — NON-NEGOTIABLE]");
        bundle.Prompt.ShouldContain("platform's response contract");
        bundle.Prompt.ShouldContain("platform-layer instructions");
    }

    /// <summary>
    /// #2670 anti-regression: the bundle must not duplicate the
    /// platform-generated tooling instructions. Naming the
    /// fundamental-core tools inline here would re-introduce the
    /// duplication this issue fixed.
    /// </summary>
    [Fact]
    public async Task Bundle_PromptDoesNotReNameFundamentalCoreTools()
    {
        var resolver = BuildResolverFromRepoPackages();

        var bundle = await resolver.ResolveAsync(
            DefaultAgentSkillBundles.ConversationalDefaults,
            TestContext.Current.CancellationToken);

        // ADR-0056 §8 fundamental-core list — every name. Layer 1
        // (PlatformPromptProvider) owns the catalog; the bundle is
        // package-specific guidance and must not re-name the core
        // surface. (sv.tools.list is excluded from the prefix
        // sweep because the discovery pointer "sv.tools.list(memory)"
        // is the bundle's own package-specific contribution.)
        var coreTools = new[]
        {
            "sv.messaging.send",
            "sv.messaging.multicast",
            "sv.directory.list",
            "sv.directory.lookup",
            "sv.progress.report",
            "sv.tools.list_categories",
        };
        foreach (var name in coreTools)
        {
            bundle.Prompt.ShouldNotContain(name,
                customMessage: $"#2670: the conversational-defaults bundle must not duplicate the platform-generated naming of {name}.");
        }
    }

    /// <summary>
    /// The bundle's only bundle-specific contribution after #2670 is
    /// the <c>memory</c> category grant. It must surface the
    /// category by name and point at <c>sv.tools.list(memory)</c>
    /// for the on-demand definitions, so the runtime can pull memory
    /// tools without first calling <c>sv.tools.list_categories</c>.
    /// </summary>
    [Fact]
    public async Task Bundle_PromptSurfacesMemoryCategoryAndDiscoveryPointer()
    {
        var resolver = BuildResolverFromRepoPackages();

        var bundle = await resolver.ResolveAsync(
            DefaultAgentSkillBundles.ConversationalDefaults,
            TestContext.Current.CancellationToken);

        bundle.Prompt.ShouldContain("memory");
        bundle.Prompt.ShouldContain("sv.tools.list(memory)");
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
