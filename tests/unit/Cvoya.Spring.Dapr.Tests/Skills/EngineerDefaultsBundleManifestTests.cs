// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System.IO;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Skills;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// On-disk verification that the in-tree
/// <c>packages/engineer-defaults/</c> bundle (#2745) holds to its
/// contract:
/// <list type="bullet">
///   <item>resolves through <see cref="FileSystemSkillBundleResolver"/>
///   under the canonical <c>spring-voyage/</c> namespace;</item>
///   <item>names the engineer-specific shell-tooling footguns the
///   platform-layer concurrent-threads guard intentionally dropped
///   when it was rewritten as the universal core; and</item>
///   <item>does not re-state the platform-layer constraints — it is
///   additive on top, not a parallel rule book.</item>
/// </list>
/// </summary>
public class EngineerDefaultsBundleManifestTests
{
    private static readonly SkillBundleReference EngineerDefaults =
        new(Package: "spring-voyage/engineer-defaults", Skill: "engineer-defaults");

    [Fact]
    public async Task ResolveAsync_LoadsBundleFromInTreePackage()
    {
        var resolver = BuildResolverFromRepoPackages();

        var bundle = await resolver.ResolveAsync(
            EngineerDefaults,
            TestContext.Current.CancellationToken);

        bundle.PackageName.ShouldBe("spring-voyage/engineer-defaults");
        bundle.SkillName.ShouldBe("engineer-defaults");
    }

    [Fact]
    public async Task Bundle_RequiredToolsListIsEmpty()
    {
        // The bundle is prompt-only — it grants no tools beyond the
        // platform core. The `.tools.json` is the empty array.
        var resolver = BuildResolverFromRepoPackages();

        var bundle = await resolver.ResolveAsync(
            EngineerDefaults,
            TestContext.Current.CancellationToken);

        bundle.RequiredTools.ShouldBeEmpty();
    }

    [Fact]
    public async Task Bundle_PromptNamesEngineerSpecificShellFootguns()
    {
        // The whole point of the bundle is to carry the SE-specific
        // tool names that the universal guard (#2745) intentionally
        // drops. Pin the names so a future reword doesn't silently
        // remove them.
        var resolver = BuildResolverFromRepoPackages();

        var bundle = await resolver.ResolveAsync(
            EngineerDefaults,
            TestContext.Current.CancellationToken);

        foreach (var name in new[]
        {
            "pytest --watch",
            "npm run dev",
            "cargo watch",
            "dotnet watch run",
            "pkill",
            "killall",
        })
        {
            bundle.Prompt.ShouldContain(
                name,
                customMessage: $"engineer-defaults bundle must name `{name}` — the universal guard intentionally dropped it (#2745).");
        }
    }

    [Fact]
    public async Task Bundle_PromptDoesNotReStatePlatformLayerConstraints()
    {
        // The bundle is additive on top of the platform-layer
        // concurrent-threads guard. Re-stating the platform-layer
        // constraints (workspace subtree, session storage, ephemeral
        // ports) would be the duplication this carve-out exists to
        // avoid.
        var resolver = BuildResolverFromRepoPackages();

        var bundle = await resolver.ResolveAsync(
            EngineerDefaults,
            TestContext.Current.CancellationToken);

        // The bundle MAY refer to the platform-layer constraints
        // ("ephemeral ports", "no process-global mutation") in passing,
        // but must not carry a parallel rule list. We pin the absence
        // of the platform-layer section heading the platform-prompt
        // provider owns.
        bundle.Prompt.ShouldNotContain(LauncherPromptFragments.ConcurrentConversationsGuardAnchor,
            customMessage: "engineer-defaults must not duplicate the platform-layer guard's section heading.");
    }

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
