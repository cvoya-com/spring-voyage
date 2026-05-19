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
/// Regression tests for the on-disk shape of <c>packages/spring-voyage-oss/</c>.
/// The package was flattened to a single unit (#2525): all engineer and PM
/// agents attach directly to <c>spring-voyage-oss</c>; the
/// <c>sv-oss-software-engineering</c> and <c>sv-oss-program-management</c>
/// sub-units were removed. These tests lock that shape against the install
/// pipeline so a future re-introduction of intermediate sub-units fails
/// loudly here before the install pipeline silently re-parents anything.
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
            "every OSS unit must resolve to a non-null execution.image after the flatten (#2525)");

        // The single unit inherits the package-level image. No sub-units exist
        // after #2525 — engineer / PM containers come from the agent templates
        // and are not part of ExecutionDefaultsResolver's unit map.
        execution.ByUnit["spring-voyage-oss"].Image
            .ShouldBe("ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest");
    }

    /// <summary>
    /// Regression test for #2525: the OSS package ships exactly one unit
    /// (<c>spring-voyage-oss</c>) with engineer / PM agents attached
    /// directly. The pre-#2525 layout nested <c>sv-oss-software-engineering</c>
    /// and <c>sv-oss-program-management</c> sub-units under the umbrella;
    /// this test fails before the install pipeline silently re-introduces
    /// any intermediate sub-unit.
    /// </summary>
    [Fact]
    public async Task SpringVoyageOss_HasSingleFlatUnit_PerFlattenInIssue2525()
    {
        var packageRoot = LocatePackageRoot();
        var yaml = await File.ReadAllTextAsync(
            Path.Combine(packageRoot, "package.yaml"),
            TestContext.Current.CancellationToken);

        var resolved = await PackageManifestParser.ParseAndResolveAsync(
            yaml, packageRoot,
            cancellationToken: TestContext.Current.CancellationToken);

        resolved.Units.Count.ShouldBe(1,
            "after #2525 the OSS package ships a single flat unit");

        var unit = resolved.Units.Single();
        unit.Name.ShouldBe("spring-voyage-oss");
        unit.IsTopLevel.ShouldBeTrue(
            "the single unit is the package's top-level activatable");
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
            "The test relies on the on-disk OSS package layout to verify the #1679 / #2525 shape.");
    }
}
