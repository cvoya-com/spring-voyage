// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

using Cvoya.Spring.Host.Api.Services;
using Cvoya.Spring.Manifest;

using Shouldly;

using Xunit;

/// <summary>
/// Zip-layout tests for #3090 R1 package reconstruction. The exporter emits a
/// zip archive containing the reconstructed package tree; each entry is rooted
/// at <c>&lt;package-name&gt;/</c> so a downstream unzip reproduces a layout the
/// installer re-ingests. The reconstruction itself (reading runtime/DB config)
/// is exercised end-to-end in the integration suite; here we pin the archive
/// shape from an already-reconstructed <see cref="ReconstructedPackage"/>.
/// </summary>
public class PackageExportServiceTests
{
    [Fact]
    public void BuildResult_EmitsZipRootedAtPackageName()
    {
        var reconstructed = new ReconstructedPackage(
            PackageName: "my-export-pkg",
            Package: new PackageManifest
            {
                ApiVersion = "spring.voyage/v1",
                Kind = "Package",
                Name = "my-export-pkg",
                Description = "x",
                Version = "1.0.0",
            },
            Documents: new[]
            {
                new RenderedDocument(
                    "units/alpha/package.yaml",
                    "apiVersion: spring.voyage/v1\nkind: Unit\nname: alpha\ndescription: x\n"),
                new RenderedDocument(
                    "units/alpha/agents/worker/package.yaml",
                    "apiVersion: spring.voyage/v1\nkind: Agent\nname: worker\ndescription: x\n"),
            });

        var result = PackageExportService.BuildResult(reconstructed);

        result.ContentType.ShouldBe("application/zip");
        result.FileName.ShouldBe("my-export-pkg.zip");

        using var memory = new MemoryStream(result.Content);
        using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
        var entryNames = zip.Entries.Select(e => e.FullName).ToList();

        // Every entry rooted at <package-name>/ for a clean unzip.
        entryNames.ShouldContain("my-export-pkg/package.yaml");
        entryNames.ShouldContain("my-export-pkg/units/alpha/package.yaml");
        entryNames.ShouldContain("my-export-pkg/units/alpha/agents/worker/package.yaml");

        // The root package.yaml carries the reconstructed metadata.
        var rootEntry = zip.GetEntry("my-export-pkg/package.yaml")!;
        using var stream = rootEntry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = reader.ReadToEnd();
        content.ShouldContain("name: my-export-pkg");
        content.ShouldContain("kind: Package");
    }
}
