// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests that verify the parser against the live
/// <c>packages/</c> tree. Un-skipped by #1562 — all three packages now
/// carry a <c>package.yaml</c> conforming to ADR-0035.
/// </summary>
public class PackageManifestParserLivePackagesTests
{
    /// <summary>
    /// Walks up from the test assembly output directory until it finds the
    /// solution file, then returns the path to the named package directory.
    /// </summary>
    private static string LivePackageRoot(string packageName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SpringVoyage.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException(
                "Could not locate solution root (SpringVoyage.slnx) from " +
                $"'{AppContext.BaseDirectory}'.");
        }

        return Path.Combine(dir.FullName, "packages", packageName);
    }

    [Fact]
    public async Task ParseResearchPackage_SucceedsWithNewShape()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = LivePackageRoot("research");
        var yaml = await File.ReadAllTextAsync(Path.Combine(root, "package.yaml"), ct);

        var result = await PackageManifestParser.ParseAndResolveAsync(
            yaml, root, cancellationToken: ct);

        result.Name.ShouldBe("research");
        result.Kind.ShouldBe(PackageKind.UnitPackage);

        // Single root unit: research-team
        result.Units.Count.ShouldBe(1);
        result.Units[0].Name.ShouldBe("research-team");
        result.Units[0].Content.ShouldNotBeNull();
        result.Units[0].IsCrossPackage.ShouldBeFalse();

        // No inputs declared.
        result.InputValues.ShouldBeEmpty();
    }

    [Fact]
    public async Task ParseProductManagementPackage_SucceedsWithNewShape()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = LivePackageRoot("product-management");
        var yaml = await File.ReadAllTextAsync(Path.Combine(root, "package.yaml"), ct);

        var result = await PackageManifestParser.ParseAndResolveAsync(
            yaml, root, cancellationToken: ct);

        result.Name.ShouldBe("product-management");
        result.Kind.ShouldBe(PackageKind.UnitPackage);

        // Single root unit: product-squad
        result.Units.Count.ShouldBe(1);
        result.Units[0].Name.ShouldBe("product-squad");
        result.Units[0].Content.ShouldNotBeNull();
        result.Units[0].IsCrossPackage.ShouldBeFalse();

        // No inputs declared.
        result.InputValues.ShouldBeEmpty();
    }

    [Fact]
    public async Task ParseSpringVoyageOssPackage_SucceedsWithNewShape()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = LivePackageRoot("spring-voyage-oss");
        var yaml = await File.ReadAllTextAsync(Path.Combine(root, "package.yaml"), ct);

        // Provide all required inputs so substitution succeeds.
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["github_owner"] = "cvoya-com",
            ["github_repo"] = "spring-voyage",
            ["github_installation_id"] = "12345678",
        };

        var result = await PackageManifestParser.ParseAndResolveAsync(
            yaml, root, inputValues: inputs, cancellationToken: ct);

        result.Name.ShouldBe("spring-voyage-oss");
        result.Kind.ShouldBe(PackageKind.UnitPackage);

        // Root unit + 4 sub-units = 5 units total.
        result.Units.Count.ShouldBe(5);
        result.Units.ShouldContain(u => u.Name == "spring-voyage-oss");
        result.Units.ShouldContain(u => u.Name == "sv-oss-software-engineering");
        result.Units.ShouldContain(u => u.Name == "sv-oss-design");
        result.Units.ShouldContain(u => u.Name == "sv-oss-product-management");
        result.Units.ShouldContain(u => u.Name == "sv-oss-program-management");
        result.Units.ShouldAllBe(u => !u.IsCrossPackage);
        result.Units.ShouldAllBe(u => u.Content != null);

        // Three required inputs resolved.
        result.InputValues.Count.ShouldBe(3);
        result.InputValues["github_owner"].ShouldBe("cvoya-com");
        result.InputValues["github_repo"].ShouldBe("spring-voyage");
        result.InputValues["github_installation_id"].ShouldBe("12345678");
    }

    [Fact]
    public async Task ParseSpringVoyageOssPackage_MissingRequiredInput_ThrowsValidationException()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = LivePackageRoot("spring-voyage-oss");
        var yaml = await File.ReadAllTextAsync(Path.Combine(root, "package.yaml"), ct);

        // Supply no inputs — all three are required.
        await Should.ThrowAsync<PackageInputValidationException>(
            () => PackageManifestParser.ParseAndResolveAsync(
                yaml, root, cancellationToken: ct));
    }
}