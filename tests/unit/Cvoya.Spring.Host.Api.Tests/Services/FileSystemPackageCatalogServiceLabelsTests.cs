// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System;
using System.IO;
using System.Threading.Tasks;

using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.Host.Api.Services;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies the catalog service's <c>RequiredConnectorSummary.Defaults</c>
/// projection (issue #2780). The wizard depends on this surface to
/// pre-seed the install form, so the projection from per-artefact
/// <c>labels:</c> blocks to the wire shape must round-trip cleanly.
/// </summary>
public class FileSystemPackageCatalogServiceLabelsTests
{
    [Fact]
    public async Task GetPackageAsync_PropagatesIncludeLabelsToDefaults()
    {
        using var pkg = await CreatePackageAsync(
            packageName: "pkg-with-labels",
            packageYaml: """
                apiVersion: spring.voyage/v1
                kind: Package
                name: pkg-with-labels
                description: x
                version: 1.0.0
                """,
            unitFiles: new[]
            {
                ("alpha", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    requires:
                      - connector: github
                        labels:
                          include: [spring-voyage-team]
                          exclude: [wip]
                    """),
            });

        var service = new FileSystemPackageCatalogService(
            new PackageCatalogOptions { Root = pkg.PackagesRoot },
            NullLogger<FileSystemPackageCatalogService>.Instance);

        var detail = await service.GetPackageAsync("pkg-with-labels", TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail!.ConnectorDeclarations.Count.ShouldBe(1);
        var decl = detail.ConnectorDeclarations[0];
        decl.Type.ShouldBe("github");
        decl.Required.ShouldBeTrue();
        decl.Defaults.ShouldNotBeNull();
        decl.Defaults!.Labels.ShouldNotBeNull();
        decl.Defaults.Labels!.Include.ShouldBe(new[] { "spring-voyage-team" });
        decl.Defaults.Labels.Exclude.ShouldBe(new[] { "wip" });
    }

    [Fact]
    public async Task GetPackageAsync_NoLabelsBlock_DefaultsIsNull()
    {
        using var pkg = await CreatePackageAsync(
            packageName: "pkg-no-labels",
            packageYaml: """
                apiVersion: spring.voyage/v1
                kind: Package
                name: pkg-no-labels
                description: x
                version: 1.0.0
                """,
            unitFiles: new[]
            {
                ("alpha", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    requires:
                      - connector: github
                    """),
            });

        var service = new FileSystemPackageCatalogService(
            new PackageCatalogOptions { Root = pkg.PackagesRoot },
            NullLogger<FileSystemPackageCatalogService>.Instance);

        var detail = await service.GetPackageAsync("pkg-no-labels", TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail!.ConnectorDeclarations.Count.ShouldBe(1);
        detail.ConnectorDeclarations[0].Type.ShouldBe("github");
        detail.ConnectorDeclarations[0].Defaults.ShouldBeNull();
    }

    [Fact]
    public async Task GetPackageAsync_ConflictingArtefacts_DropsDefaultsButReportsSlug()
    {
        // The catalog service is a hot-path read surface — it must not
        // throw on a package whose authors disagree. It logs a warning,
        // drops the pre-seed, and keeps the declaration so the wizard
        // still renders a connector step (the operator fills the filter
        // manually). The parser at install time will still reject.
        using var pkg = await CreatePackageAsync(
            packageName: "pkg-conflict",
            packageYaml: """
                apiVersion: spring.voyage/v1
                kind: Package
                name: pkg-conflict
                description: x
                version: 1.0.0
                """,
            unitFiles: new[]
            {
                ("alpha", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    requires:
                      - connector: github
                        labels:
                          include: [team-a]
                    """),
                ("beta", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: beta
                    description: x
                    requires:
                      - connector: github
                        labels:
                          include: [team-b]
                    """),
            });

        var service = new FileSystemPackageCatalogService(
            new PackageCatalogOptions { Root = pkg.PackagesRoot },
            NullLogger<FileSystemPackageCatalogService>.Instance);

        var detail = await service.GetPackageAsync("pkg-conflict", TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail!.ConnectorDeclarations.Count.ShouldBe(1);
        detail.ConnectorDeclarations[0].Type.ShouldBe("github");
        // Conflict drops the pre-seed so the wizard surfaces no defaults.
        detail.ConnectorDeclarations[0].Defaults.ShouldBeNull();
    }

    // ── Helper: minimal on-disk packages root + one package ──────────────

    private static async Task<TempPackagesRoot> CreatePackageAsync(
        string packageName,
        string packageYaml,
        (string Name, string Content)[] unitFiles)
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "sv-2780-catalog-" + Path.GetRandomFileName());
        Directory.CreateDirectory(rootDir);

        var packageRoot = Path.Combine(rootDir, packageName);
        Directory.CreateDirectory(packageRoot);
        var unitsDir = Path.Combine(packageRoot, "units");
        Directory.CreateDirectory(unitsDir);

        var packagePath = Path.Combine(packageRoot, "package.yaml");
        await File.WriteAllTextAsync(packagePath, packageYaml);

        foreach (var (name, content) in unitFiles)
        {
            var unitDir = Path.Combine(unitsDir, name);
            Directory.CreateDirectory(unitDir);
            await File.WriteAllTextAsync(Path.Combine(unitDir, "package.yaml"), content);
        }

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
