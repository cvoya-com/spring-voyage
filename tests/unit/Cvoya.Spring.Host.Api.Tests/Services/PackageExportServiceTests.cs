// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

using Cvoya.Spring.Host.Api.Services;

using Shouldly;

using Xunit;

/// <summary>
/// Round-trip tests for ADR-0043 recursive package export. The exporter
/// emits a zip archive containing the package's folder tree; each entry
/// is rooted at <c>&lt;package-name&gt;/</c> so a downstream unzip
/// reproduces the layout the install ingested.
/// </summary>
public class PackageExportServiceTests
{
    [Fact]
    public void BuildResult_WithPackageRoot_EmitsZipWithRecursiveLayout()
    {
        // Lay down a small ADR-0043-shaped package on disk and export.
        var root = Path.Combine(Path.GetTempPath(), "sv-export-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "package.yaml"), """
                apiVersion: spring.voyage/v1
                kind: Package
                name: my-export-pkg
                description: x
                version: 1.0.0
                """);
            File.WriteAllText(Path.Combine(root, "README.md"), "# my-export-pkg");

            var unitDir = Path.Combine(root, "units", "alpha");
            Directory.CreateDirectory(unitDir);
            File.WriteAllText(Path.Combine(unitDir, "package.yaml"), """
                apiVersion: spring.voyage/v1
                kind: Unit
                name: alpha
                description: x
                """);

            var agentDir = Path.Combine(root, "units", "alpha", "agents", "worker");
            Directory.CreateDirectory(agentDir);
            File.WriteAllText(Path.Combine(agentDir, "package.yaml"), """
                apiVersion: spring.voyage/v1
                kind: Agent
                name: worker
                description: x
                """);

            var result = PackageExportService.BuildResult(
                packageName: "my-export-pkg",
                originalYaml: "(ignored when packageRoot is supplied)",
                packageRoot: root);

            result.ContentType.ShouldBe("application/zip");
            result.FileName.ShouldBe("my-export-pkg.zip");

            // Inspect the zip entries.
            using var memory = new MemoryStream(result.Content);
            using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
            var entryNames = zip.Entries.Select(e => e.FullName).ToList();

            // Every entry rooted at <package-name>/ for a clean unzip.
            entryNames.ShouldContain("my-export-pkg/package.yaml");
            entryNames.ShouldContain("my-export-pkg/README.md");
            entryNames.ShouldContain("my-export-pkg/units/alpha/package.yaml");
            entryNames.ShouldContain("my-export-pkg/units/alpha/agents/worker/package.yaml");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void BuildResult_WithoutPackageRoot_EmitsFallbackZip()
    {
        // No package root → one-file zip with just the root package.yaml.
        var result = PackageExportService.BuildResult(
            packageName: "upload-only",
            originalYaml: "apiVersion: spring.voyage/v1\nkind: Package\nname: upload-only\ndescription: x\nversion: 1.0.0\n",
            packageRoot: null);

        result.ContentType.ShouldBe("application/zip");
        result.FileName.ShouldBe("upload-only.zip");

        using var memory = new MemoryStream(result.Content);
        using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
        var entries = zip.Entries.ToList();
        entries.Count.ShouldBe(1);
        entries[0].FullName.ShouldBe("upload-only/package.yaml");

        using var stream = entries[0].Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = reader.ReadToEnd();
        content.ShouldContain("name: upload-only");
    }

    [Fact]
    public void BuildResult_SkipsHiddenAndBuildDirs()
    {
        // Ensure .git/, bin/, obj/, node_modules/ don't pollute the zip.
        var root = Path.Combine(Path.GetTempPath(), "sv-export-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "package.yaml"), """
                apiVersion: spring.voyage/v1
                kind: Package
                name: filtered
                description: x
                version: 1.0.0
                """);

            // Junk dirs that must be excluded.
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/main");
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            File.WriteAllText(Path.Combine(root, "bin", "junk.dll"), "binary");
            Directory.CreateDirectory(Path.Combine(root, "node_modules"));
            File.WriteAllText(Path.Combine(root, "node_modules", "pkg.json"), "{}");

            var result = PackageExportService.BuildResult("filtered", "ignored", root);

            using var memory = new MemoryStream(result.Content);
            using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            names.ShouldContain("filtered/package.yaml");
            names.ShouldNotContain("filtered/.git/HEAD");
            names.ShouldNotContain("filtered/bin/junk.dll");
            names.ShouldNotContain("filtered/node_modules/pkg.json");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
