// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Host.Api.Services;
using Cvoya.Spring.Manifest;

using Shouldly;

using Xunit;

/// <summary>
/// Regression test for the <c>packages/spring-voyage-oss/</c> migration
/// to package-level <see cref="PackageManifest.Execution"/> inheritance
/// (#1679). Verifies the resolved per-unit execution defaults: the
/// umbrella unit inherits the package-level image and the two
/// sub-units (<c>sv-oss-software-engineering</c>,
/// <c>sv-oss-program-management</c>) carry their own team-specific
/// image overrides.
/// </summary>
public class SpringVoyageOssMigrationTests
{
    [Fact]
    public async Task SpringVoyageOss_ExecutionDefaults_ResolvedPerUnit()
    {
        var packageRoot = LocatePackageRoot();
        var yaml = await File.ReadAllTextAsync(
            Path.Combine(packageRoot, "package.yaml"),
            TestContext.Current.CancellationToken);

        var resolved = await PackageManifestParser.ParseAndResolveAsync(
            yaml, packageRoot,
            cancellationToken: TestContext.Current.CancellationToken);

        var execution = ExecutionDefaultsResolver.Resolve(resolved);

        execution.Missing.ShouldBeEmpty(
            "every OSS member unit must resolve to a non-null execution.image after migration");

        // Umbrella inherits the package-level image.
        execution.ByUnit["spring-voyage-oss"].Image
            .ShouldBe("ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest");

        // Each sub-unit keeps its own team-specific image.
        execution.ByUnit["sv-oss-software-engineering"].Image
            .ShouldBe("ghcr.io/cvoya-com/spring-voyage-agent-oss-software-engineering:latest");
        execution.ByUnit["sv-oss-program-management"].Image
            .ShouldBe("ghcr.io/cvoya-com/spring-voyage-agent-oss-program-management:latest");
    }

    /// <summary>
    /// Regression test for #2439: ADR-0043 § 1 requires the sub-units to
    /// nest under the umbrella's <c>units/</c> subdirectory rather than
    /// sit as flat siblings of it. Locks in the membership graph the
    /// install pipeline sees from disk: the umbrella resolves as the
    /// package's single top-level unit, and each sub-unit's
    /// <see cref="ResolvedArtefact.ContainingArtefactName"/> points at
    /// the umbrella. If a future package edit re-flattens the layout,
    /// this test fails before the install pipeline silently re-parents
    /// the sub-units to the tenant scope.
    /// </summary>
    [Fact]
    public async Task SpringVoyageOss_SubUnitsNestUnderUmbrella_PerAdr0043()
    {
        var packageRoot = LocatePackageRoot();
        var yaml = await File.ReadAllTextAsync(
            Path.Combine(packageRoot, "package.yaml"),
            TestContext.Current.CancellationToken);

        var resolved = await PackageManifestParser.ParseAndResolveAsync(
            yaml, packageRoot,
            cancellationToken: TestContext.Current.CancellationToken);

        resolved.Units.Count.ShouldBe(3,
            "the OSS package ships three units: the umbrella and its two sub-units");

        var umbrella = resolved.Units.Single(u => u.Name == "spring-voyage-oss");
        umbrella.IsTopLevel.ShouldBeTrue(
            "the umbrella is the package's single top-level activatable");

        var swEng = resolved.Units.Single(u => u.Name == "sv-oss-software-engineering");
        swEng.IsTopLevel.ShouldBeFalse(
            "software-engineering nests under the umbrella per ADR-0043 § 1");
        swEng.ContainingArtefactName.ShouldBe("spring-voyage-oss");

        var pgmMgmt = resolved.Units.Single(u => u.Name == "sv-oss-program-management");
        pgmMgmt.IsTopLevel.ShouldBeFalse(
            "program-management nests under the umbrella per ADR-0043 § 1");
        pgmMgmt.ContainingArtefactName.ShouldBe("spring-voyage-oss");
    }

    [Fact]
    public async Task SpringVoyageOss_PackageExecution_DeclaredAtPackageLevel()
    {
        var packageRoot = LocatePackageRoot();
        var yaml = await File.ReadAllTextAsync(
            Path.Combine(packageRoot, "package.yaml"),
            TestContext.Current.CancellationToken);

        var resolved = await PackageManifestParser.ParseAndResolveAsync(
            yaml, packageRoot,
            cancellationToken: TestContext.Current.CancellationToken);

        resolved.Execution.ShouldNotBeNull(
            "the OSS package declares an execution: block under #1679");
        resolved.Execution!.Image
            .ShouldBe("ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest");
        // No `inherit:` selector — every member participates by default.
        resolved.Execution.InheritUnits.ShouldBeNull();
    }

    private static string LocatePackageRoot()
    {
        // Walk up from the test binary location to find the repo root,
        // then the OSS package directory. The test binary lives under
        // tests/unit/Cvoya.Spring.Host.Api.Tests/bin/<config>/<tfm>/, so the
        // repo root is five levels up.
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "packages", "spring-voyage-oss");
            if (Directory.Exists(candidate)
                && File.Exists(Path.Combine(candidate, "package.yaml")))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate packages/spring-voyage-oss/ from the test binary path. " +
            "The test relies on the on-disk OSS package layout to verify the #1679 migration.");
    }
}
