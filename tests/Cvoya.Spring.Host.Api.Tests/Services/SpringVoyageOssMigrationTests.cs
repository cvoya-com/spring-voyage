// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System.IO;
using System.Threading.Tasks;

using Cvoya.Spring.Host.Api.Services;
using Cvoya.Spring.Manifest;

using Shouldly;

using Xunit;

/// <summary>
/// Regression test for the <c>packages/spring-voyage-oss/</c> migration
/// to package-level <see cref="PackageManifest.Execution"/> inheritance
/// (#1679). Verifies the resolved per-unit execution defaults stay
/// stable after the migration: every member unit's image resolves to the
/// same value the pre-migration manifest produced, the umbrella unit
/// inherits the package-level image, and the four sub-units still carry
/// their team-specific image overrides.
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
            .ShouldBe("ghcr.io/cvoya-com/spring-voyage-agents:latest");

        // Each sub-unit keeps its own team-specific image.
        execution.ByUnit["sv-oss-software-engineering"].Image
            .ShouldBe("ghcr.io/cvoya-com/spring-voyage-agent-oss-software-engineering:latest");
        execution.ByUnit["sv-oss-design"].Image
            .ShouldBe("ghcr.io/cvoya-com/spring-voyage-agent-oss-design:latest");
        execution.ByUnit["sv-oss-product-management"].Image
            .ShouldBe("ghcr.io/cvoya-com/spring-voyage-agent-oss-product-management:latest");
        execution.ByUnit["sv-oss-program-management"].Image
            .ShouldBe("ghcr.io/cvoya-com/spring-voyage-agent-oss-program-management:latest");
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
            .ShouldBe("ghcr.io/cvoya-com/spring-voyage-agents:latest");
        // No `inherit:` selector — every member participates by default.
        resolved.Execution.InheritUnits.ShouldBeNull();
    }

    private static string LocatePackageRoot()
    {
        // Walk up from the test binary location to find the repo root,
        // then the OSS package directory. The test binary lives under
        // tests/Cvoya.Spring.Host.Api.Tests/bin/<config>/<tfm>/, so the
        // repo root is four levels up.
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