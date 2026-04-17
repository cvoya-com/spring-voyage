// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Host.Api.Services;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="FileSystemPackageCatalogService"/>. Drives
/// the browse surface end-to-end against a throwaway temp packages
/// tree, so the tests exercise the layout the service actually walks
/// rather than relying on the committed <c>packages/</c> directory
/// that could drift.
/// </summary>
public sealed class FileSystemPackageCatalogServiceTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemPackageCatalogService _service;

    public FileSystemPackageCatalogServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "spring-voyage-pkg-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _service = new FileSystemPackageCatalogService(
            new PackageCatalogOptions { Root = _root },
            NullLogger<FileSystemPackageCatalogService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Tests run in isolated dirs; cleanup failure is not fatal.
        }
    }

    [Fact]
    public async Task ListPackagesAsync_ReturnsEmpty_WhenRootMissing()
    {
        var missingRoot = Path.Combine(_root, "does-not-exist");
        var svc = new FileSystemPackageCatalogService(
            new PackageCatalogOptions { Root = missingRoot },
            NullLogger<FileSystemPackageCatalogService>.Instance);

        var result = await svc.ListPackagesAsync(CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListPackagesAsync_CountsEachContentType()
    {
        var pkg = SeedPackage("example");
        File.WriteAllText(
            Path.Combine(pkg, "units", "alpha.yaml"),
            "unit:\n  name: alpha\n  description: example unit\n");
        File.WriteAllText(
            Path.Combine(pkg, "agents", "worker.yaml"),
            "agent:\n  id: worker\n  name: Worker\n  role: worker\n");
        File.WriteAllText(
            Path.Combine(pkg, "skills", "triage.md"),
            "## Triage\n");
        File.WriteAllText(
            Path.Combine(pkg, "skills", "triage.tools.json"),
            "[]");
        File.WriteAllText(
            Path.Combine(pkg, "connectors", "placeholder.txt"), "x");
        Directory.CreateDirectory(Path.Combine(pkg, "workflows", "flow"));

        // A README's first non-heading line becomes the description.
        File.WriteAllText(
            Path.Combine(pkg, "README.md"),
            "# Example\n\nA fixture package used by tests.\n");

        var result = await _service.ListPackagesAsync(CancellationToken.None);

        result.Count.ShouldBe(1);
        var summary = result[0];
        summary.Name.ShouldBe("example");
        summary.Description.ShouldBe("A fixture package used by tests.");
        summary.UnitTemplateCount.ShouldBe(1);
        summary.AgentTemplateCount.ShouldBe(1);
        summary.SkillCount.ShouldBe(1);
        summary.ConnectorCount.ShouldBe(1);
        summary.WorkflowCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetPackageAsync_ReturnsFullDetail()
    {
        var pkg = SeedPackage("example");
        File.WriteAllText(
            Path.Combine(pkg, "units", "alpha.yaml"),
            "unit:\n  name: alpha\n  description: example unit\n");
        File.WriteAllText(
            Path.Combine(pkg, "agents", "worker.yaml"),
            "agent:\n  id: worker\n  name: Worker\n  role: worker\n  instructions: do the work.\n");
        File.WriteAllText(
            Path.Combine(pkg, "skills", "triage.md"),
            "## Triage\n");

        var detail = await _service.GetPackageAsync("example", CancellationToken.None);

        detail.ShouldNotBeNull();
        detail!.Name.ShouldBe("example");
        detail.UnitTemplates.Count.ShouldBe(1);
        detail.UnitTemplates[0].Name.ShouldBe("alpha");
        detail.UnitTemplates[0].Description.ShouldBe("example unit");
        detail.AgentTemplates.Count.ShouldBe(1);
        detail.AgentTemplates[0].Name.ShouldBe("worker");
        detail.AgentTemplates[0].DisplayName.ShouldBe("Worker");
        detail.AgentTemplates[0].Role.ShouldBe("worker");
        detail.AgentTemplates[0].Description.ShouldBe("do the work.");
        detail.Skills.Count.ShouldBe(1);
        detail.Skills[0].Name.ShouldBe("triage");
        detail.Skills[0].HasTools.ShouldBeFalse();
    }

    [Fact]
    public async Task GetPackageAsync_ReturnsNull_ForMissingPackage()
    {
        var result = await _service.GetPackageAsync("no-such-pkg", CancellationToken.None);

        result.ShouldBeNull();
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("nested/name")]
    public async Task GetPackageAsync_RejectsTraversalAttempts(string name)
    {
        // The browse surface is Viewer-gated and the catalog lives on
        // disk. Allowing path-traversal names would let a caller list
        // arbitrary directories above `Packages:Root`.
        var result = await _service.GetPackageAsync(name, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ListUnitTemplatesAsync_ReturnsTemplatesAcrossPackages()
    {
        var pkgA = SeedPackage("alpha");
        File.WriteAllText(
            Path.Combine(pkgA, "units", "one.yaml"),
            "unit:\n  name: one\n");
        var pkgB = SeedPackage("bravo");
        File.WriteAllText(
            Path.Combine(pkgB, "units", "two.yaml"),
            "unit:\n  name: two\n");

        var result = await _service.ListUnitTemplatesAsync(CancellationToken.None);

        result.Count.ShouldBe(2);
        result.ShouldContain(t => t.Package == "alpha" && t.Name == "one");
        result.ShouldContain(t => t.Package == "bravo" && t.Name == "two");
    }

    [Fact]
    public async Task LoadUnitTemplateYamlAsync_ReturnsRawYaml()
    {
        var pkg = SeedPackage("example");
        var yaml = "unit:\n  name: alpha\n";
        File.WriteAllText(Path.Combine(pkg, "units", "alpha.yaml"), yaml);

        var result = await _service.LoadUnitTemplateYamlAsync(
            "example", "alpha", CancellationToken.None);

        result.ShouldBe(yaml);
    }

    [Fact]
    public async Task LoadUnitTemplateYamlAsync_ReturnsNull_ForUnknownTemplate()
    {
        var result = await _service.LoadUnitTemplateYamlAsync(
            "no-such", "no-such", CancellationToken.None);

        result.ShouldBeNull();
    }

    private string SeedPackage(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(dir, "units"));
        Directory.CreateDirectory(Path.Combine(dir, "agents"));
        Directory.CreateDirectory(Path.Combine(dir, "skills"));
        Directory.CreateDirectory(Path.Combine(dir, "connectors"));
        Directory.CreateDirectory(Path.Combine(dir, "workflows"));
        return dir;
    }
}