// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System;
using System.IO;
using System.Threading.Tasks;

using Cvoya.Spring.Host.Api.Services;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies the catalog service surfaces a package manifest's optional
/// top-level "displayName:" on both the list (PackageSummary) and detail
/// (PackageDetail) projections, and reports null when the manifest omits it
/// or sets it to whitespace — consumers then fall back to the package name.
/// </summary>
public class FileSystemPackageCatalogServiceDisplayNameTests
{
    [Fact]
    public async Task ListPackagesAsync_SurfacesManifestDisplayName()
    {
        using var pkg = await CreatePackageAsync(
            "magazine",
            """
            apiVersion: spring.voyage/v1
            kind: Package
            name: magazine
            displayName: Magazine
            description: x
            version: 1.0.0
            """);

        var summaries = await NewService(pkg.PackagesRoot)
            .ListPackagesAsync(TestContext.Current.CancellationToken);

        var summary = summaries.ShouldHaveSingleItem();
        summary.Name.ShouldBe("magazine");
        summary.DisplayName.ShouldBe("Magazine");
    }

    [Fact]
    public async Task ListPackagesAsync_NoDisplayName_IsNull()
    {
        using var pkg = await CreatePackageAsync(
            "plain",
            """
            apiVersion: spring.voyage/v1
            kind: Package
            name: plain
            description: x
            version: 1.0.0
            """);

        var summaries = await NewService(pkg.PackagesRoot)
            .ListPackagesAsync(TestContext.Current.CancellationToken);

        summaries.ShouldHaveSingleItem().DisplayName.ShouldBeNull();
    }

    [Fact]
    public async Task GetPackageAsync_SurfacesManifestDisplayName()
    {
        using var pkg = await CreatePackageAsync(
            "magazine",
            """
            apiVersion: spring.voyage/v1
            kind: Package
            name: magazine
            displayName: Magazine
            description: x
            version: 1.0.0
            """);

        var detail = await NewService(pkg.PackagesRoot)
            .GetPackageAsync("magazine", TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail!.DisplayName.ShouldBe("Magazine");
    }

    [Fact]
    public async Task GetPackageAsync_WhitespaceDisplayName_IsNull()
    {
        // A whitespace-only displayName is treated as absent so consumers
        // fall back to the package name rather than rendering a blank label.
        using var pkg = await CreatePackageAsync(
            "blanky",
            "apiVersion: spring.voyage/v1\n"
            + "kind: Package\n"
            + "name: blanky\n"
            + "displayName: \"   \"\n"
            + "description: x\n"
            + "version: 1.0.0\n");

        var detail = await NewService(pkg.PackagesRoot)
            .GetPackageAsync("blanky", TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail!.DisplayName.ShouldBeNull();
    }

    private static FileSystemPackageCatalogService NewService(string root) =>
        new(
            new PackageCatalogOptions { Root = root },
            NullLogger<FileSystemPackageCatalogService>.Instance);

    private static async Task<TempPackagesRoot> CreatePackageAsync(
        string packageName,
        string packageYaml)
    {
        var rootDir = Path.Combine(
            Path.GetTempPath(), "sv-pkg-displayname-" + Path.GetRandomFileName());
        Directory.CreateDirectory(rootDir);

        var packageRoot = Path.Combine(rootDir, packageName);
        Directory.CreateDirectory(packageRoot);
        await File.WriteAllTextAsync(
            Path.Combine(packageRoot, "package.yaml"), packageYaml);

        return new TempPackagesRoot(rootDir);
    }

    private sealed class TempPackagesRoot : IDisposable
    {
        public string PackagesRoot { get; }

        public TempPackagesRoot(string packagesRoot) => PackagesRoot = packagesRoot;

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(PackagesRoot))
                {
                    Directory.Delete(PackagesRoot, recursive: true);
                }
            }
            catch { /* best effort */ }
        }
    }
}
