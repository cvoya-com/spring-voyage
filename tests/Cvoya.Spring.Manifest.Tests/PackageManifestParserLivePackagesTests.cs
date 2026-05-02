// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using Xunit;

/// <summary>
/// Forward-looking integration tests that verify the parser against the live
/// <c>packages/</c> tree. These tests are skipped until #1562 migrates the
/// packages to the new <c>package.yaml</c> shape.
/// </summary>
/// <remarks>
/// Acceptance criterion from #1557: "Parses <c>packages/research/</c>,
/// <c>packages/product-management/</c>, <c>packages/spring-voyage-oss/</c>
/// against the new shape (after #6 migration)." — this lights up after #1562.
/// </remarks>
public class PackageManifestParserLivePackagesTests
{
    [Fact(Skip = "Lights up after #1562 migrates packages/ to package.yaml shape.")]
    public void ParseResearchPackage_SucceedsWithNewShape()
    {
        // TODO: wire once #1562 ships.
        // var root = RepoRoot("packages/research");
        // var yaml = File.ReadAllText(Path.Combine(root, "package.yaml"));
        // var result = PackageManifestParser.ParseAndResolveAsync(yaml, root).GetAwaiter().GetResult();
        // result.Name.ShouldBe("research");
    }

    [Fact(Skip = "Lights up after #1562 migrates packages/ to package.yaml shape.")]
    public void ParseProductManagementPackage_SucceedsWithNewShape()
    {
        // TODO: wire once #1562 ships.
    }

    [Fact(Skip = "Lights up after #1562 migrates packages/ to package.yaml shape.")]
    public void ParseSpringVoyageOssPackage_SucceedsWithNewShape()
    {
        // TODO: wire once #1562 ships.
    }
}