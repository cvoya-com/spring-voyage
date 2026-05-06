// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using System.IO;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

/// <summary>
/// Parse / resolve tests for the package-level <c>execution:</c> block
/// (#1679). Covers the schema delta on <see cref="PackageManifest"/>,
/// the <c>inherit:</c> child key shape (omitted | <c>all</c> |
/// <c>[list]</c>), and the per-unit projection through
/// <see cref="ResolvedPackage.Execution"/>.
/// </summary>
public class PackageExecutionInheritanceTests
{
    [Fact]
    public void ParseRaw_PackageLevelExecution_PreservesFields()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Package
            name: my-package
            description: x
            version: 1.0.0
            content:
              - unit: my-unit
            execution:
              image: ghcr.io/example/agent:latest
              runtime: docker
              provider: anthropic
              model: claude-opus-4-7
            """;

        var manifest = PackageManifestParser.ParseRaw(yaml);

        manifest.Execution.ShouldNotBeNull();
        manifest.Execution!.Image.ShouldBe("ghcr.io/example/agent:latest");
        manifest.Execution.Runtime.ShouldBe("docker");
        manifest.Execution.Provider.ShouldBe("anthropic");
        manifest.Execution.Model.ShouldBe("claude-opus-4-7");
    }

    [Fact]
    public void ParseRaw_PackageLevelExecution_WithInheritAll_AcceptedAsScalar()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Package
            name: my-package
            description: x
            version: 1.0.0
            content:
              - unit: my-unit
            execution:
              image: ghcr.io/example/agent:latest
              inherit: all
            """;

        var manifest = PackageManifestParser.ParseRaw(yaml);

        manifest.Execution.ShouldNotBeNull();
        manifest.Execution!.Inherit.ShouldNotBeNull();
        manifest.Execution.Inherit.ShouldBeOfType<string>().ShouldBe("all");
    }

    // ---- Resolved-shape tests (require on-disk fixtures) ---------------

    [Fact]
    public async Task ParseAndResolve_PackageExecution_ProjectedOntoResolvedPackage()
    {
        using var pkg = await CreatePackageAsync(
            packageYaml: """
                apiVersion: spring.voyage/v1
                kind: Package
                name: pkg
                description: x
                version: 1.0.0
                content:
                  - unit: alpha
                execution:
                  image: ghcr.io/example/agent:latest
                  runtime: docker
                """,
            unitFiles: new[]
            {
                ("alpha.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    """),
            });

        var resolved = await PackageManifestParser.ParseAndResolveAsync(
            pkg.PackageYaml, pkg.PackageRoot,
            cancellationToken: TestContext.Current.CancellationToken);

        resolved.Execution.ShouldNotBeNull();
        resolved.Execution!.Image.ShouldBe("ghcr.io/example/agent:latest");
        resolved.Execution.Runtime.ShouldBe("docker");
        resolved.Execution.InheritUnits.ShouldBeNull();  // every member inherits
    }

    [Fact]
    public async Task ParseAndResolve_PackageExecution_AbsentBlock_ResolvesNull()
    {
        using var pkg = await CreatePackageAsync(
            packageYaml: """
                apiVersion: spring.voyage/v1
                kind: Package
                name: pkg
                description: x
                version: 1.0.0
                content:
                  - unit: alpha
                """,
            unitFiles: new[]
            {
                ("alpha.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    execution:
                      image: ghcr.io/example/agent:latest
                    """),
            });

        var resolved = await PackageManifestParser.ParseAndResolveAsync(
            pkg.PackageYaml, pkg.PackageRoot,
            cancellationToken: TestContext.Current.CancellationToken);

        resolved.Execution.ShouldBeNull();
    }

    [Fact]
    public async Task ParseAndResolve_InheritAll_NormalisesToNullInheritUnits()
    {
        using var pkg = await CreatePackageAsync(
            packageYaml: """
                apiVersion: spring.voyage/v1
                kind: Package
                name: pkg
                description: x
                version: 1.0.0
                content:
                  - unit: alpha
                execution:
                  image: ghcr.io/example/agent:latest
                  inherit: all
                """,
            unitFiles: new[]
            {
                ("alpha.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    """),
            });

        var resolved = await PackageManifestParser.ParseAndResolveAsync(
            pkg.PackageYaml, pkg.PackageRoot,
            cancellationToken: TestContext.Current.CancellationToken);

        resolved.Execution.ShouldNotBeNull();
        resolved.Execution!.InheritUnits.ShouldBeNull();
    }

    [Fact]
    public async Task ParseAndResolve_InheritList_KeepsListInheritUnits()
    {
        using var pkg = await CreatePackageAsync(
            packageYaml: """
                apiVersion: spring.voyage/v1
                kind: Package
                name: pkg
                description: x
                version: 1.0.0
                content:
                  - unit: alpha
                  - unit: beta
                execution:
                  image: ghcr.io/example/agent:latest
                  inherit:
                    - alpha
                """,
            unitFiles: new[]
            {
                ("alpha.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    """),
                ("beta.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: beta
                    description: x
                    execution:
                      image: ghcr.io/example/beta:latest
                    """),
            });

        var resolved = await PackageManifestParser.ParseAndResolveAsync(
            pkg.PackageYaml, pkg.PackageRoot,
            cancellationToken: TestContext.Current.CancellationToken);

        resolved.Execution.ShouldNotBeNull();
        resolved.Execution!.InheritUnits.ShouldNotBeNull();
        resolved.Execution.InheritUnits!.ShouldContain("alpha");
        resolved.Execution.InheritUnits.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ParseAndResolve_InheritList_UnknownUnit_Rejected()
    {
        using var pkg = await CreatePackageAsync(
            packageYaml: """
                apiVersion: spring.voyage/v1
                kind: Package
                name: pkg
                description: x
                version: 1.0.0
                content:
                  - unit: alpha
                execution:
                  image: ghcr.io/example/agent:latest
                  inherit:
                    - alpha
                    - bogus
                """,
            unitFiles: new[]
            {
                ("alpha.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    """),
            });

        var ex = await Should.ThrowAsync<PackageParseException>(async () =>
            await PackageManifestParser.ParseAndResolveAsync(
                pkg.PackageYaml, pkg.PackageRoot,
                cancellationToken: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("'bogus'");
    }

    [Fact]
    public async Task ParseAndResolve_InheritEmptyList_Rejected()
    {
        using var pkg = await CreatePackageAsync(
            packageYaml: """
                apiVersion: spring.voyage/v1
                kind: Package
                name: pkg
                description: x
                version: 1.0.0
                content:
                  - unit: alpha
                execution:
                  image: ghcr.io/example/agent:latest
                  inherit: []
                """,
            unitFiles: new[]
            {
                ("alpha.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    """),
            });

        var ex = await Should.ThrowAsync<PackageParseException>(async () =>
            await PackageManifestParser.ParseAndResolveAsync(
                pkg.PackageYaml, pkg.PackageRoot,
                cancellationToken: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("empty sequence");
    }

    [Fact]
    public async Task ParseAndResolve_InheritUnknownScalar_Rejected()
    {
        using var pkg = await CreatePackageAsync(
            packageYaml: """
                apiVersion: spring.voyage/v1
                kind: Package
                name: pkg
                description: x
                version: 1.0.0
                content:
                  - unit: alpha
                execution:
                  image: ghcr.io/example/agent:latest
                  inherit: none
                """,
            unitFiles: new[]
            {
                ("alpha.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    """),
            });

        var ex = await Should.ThrowAsync<PackageParseException>(async () =>
            await PackageManifestParser.ParseAndResolveAsync(
                pkg.PackageYaml, pkg.PackageRoot,
                cancellationToken: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("'none'");
    }

    [Fact]
    public void PackageExecutionDeclaration_AppliesTo_NullInheritUnits_True()
    {
        var decl = new PackageExecutionDeclaration("img", null, null, null, InheritUnits: null);
        decl.AppliesTo("any-unit").ShouldBeTrue();
    }

    [Fact]
    public void PackageExecutionDeclaration_AppliesTo_ListMember_True()
    {
        var decl = new PackageExecutionDeclaration("img", null, null, null,
            InheritUnits: new[] { "alpha", "beta" });
        decl.AppliesTo("alpha").ShouldBeTrue();
        decl.AppliesTo("BETA").ShouldBeTrue(); // case-insensitive
    }

    [Fact]
    public void PackageExecutionDeclaration_AppliesTo_NotMember_False()
    {
        var decl = new PackageExecutionDeclaration("img", null, null, null,
            InheritUnits: new[] { "alpha" });
        decl.AppliesTo("beta").ShouldBeFalse();
    }

    // ---- Validator integration (the in-tree gate that runs in CI) -----

    [Fact]
    public async Task Validator_UmbrellaWithoutImage_PackageDefaultProvides_NoError()
    {
        // OSS-shaped: package declares execution.image; the umbrella
        // unit does NOT declare its own. The validator must honour the
        // package-level inheritance and not flag `unit-missing-image`.
        var packageYaml = """
            apiVersion: spring.voyage/v1
            kind: Package
            name: test-pkg
            description: x
            version: 1.0.0
            content:
              - unit: umbrella
            execution:
              image: ghcr.io/example/agent:latest
              runtime: docker
            """;
        var unitYaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: umbrella
            description: x
            """;
        using var pkg = await CreatePackageAsync(packageYaml, [("umbrella.yaml", unitYaml)]);
        var source = new Cvoya.Spring.Manifest.Validation.DirectoryPackageSource(pkg.PackageRoot);

        var result = await Cvoya.Spring.Manifest.Validation.PackageValidator.ValidateAsync(
            source, TestContext.Current.CancellationToken);

        result.Diagnostics.ShouldNotContain(d => d.Code == "unit-missing-image");
    }

    [Fact]
    public async Task Validator_UmbrellaWithoutImage_NoPackageDefault_StillErrors()
    {
        // Negative control: when neither side declares an image, the
        // per-unit error must still fire so operators can't accidentally
        // ship a unit nobody can launch.
        var packageYaml = """
            apiVersion: spring.voyage/v1
            kind: Package
            name: test-pkg
            description: x
            version: 1.0.0
            content:
              - unit: umbrella
            """;
        var unitYaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: umbrella
            description: x
            """;
        using var pkg = await CreatePackageAsync(packageYaml, [("umbrella.yaml", unitYaml)]);
        var source = new Cvoya.Spring.Manifest.Validation.DirectoryPackageSource(pkg.PackageRoot);

        var result = await Cvoya.Spring.Manifest.Validation.PackageValidator.ValidateAsync(
            source, TestContext.Current.CancellationToken);

        result.Diagnostics.ShouldContain(d => d.Code == "unit-missing-image");
    }

    [Fact]
    public async Task Validator_PackageImageButUnitNotInInheritList_StillErrors()
    {
        // Inherit: [other] → the umbrella unit is NOT in scope, so the
        // package's image does not cover it; the per-unit error must fire.
        var packageYaml = """
            apiVersion: spring.voyage/v1
            kind: Package
            name: test-pkg
            description: x
            version: 1.0.0
            content:
              - unit: umbrella
              - unit: other
            execution:
              image: ghcr.io/example/agent:latest
              inherit:
                - other
            """;
        var umbrellaYaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: umbrella
            description: x
            """;
        var otherYaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: other
            description: x
            """;
        using var pkg = await CreatePackageAsync(
            packageYaml,
            [("umbrella.yaml", umbrellaYaml), ("other.yaml", otherYaml)]);
        var source = new Cvoya.Spring.Manifest.Validation.DirectoryPackageSource(pkg.PackageRoot);

        var result = await Cvoya.Spring.Manifest.Validation.PackageValidator.ValidateAsync(
            source, TestContext.Current.CancellationToken);

        result.Diagnostics.ShouldContain(
            d => d.Code == "unit-missing-image"
                && d.Message.Contains("umbrella"));
        result.Diagnostics.ShouldNotContain(
            d => d.Code == "unit-missing-image"
                && d.Message.Contains("'other'"));
    }

    // ---- Test fixture helpers ------------------------------------------

    private static async Task<TempPackage> CreatePackageAsync(
        string packageYaml,
        (string Filename, string Content)[] unitFiles)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "sv-1679-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        var unitsDir = Path.Combine(tempRoot, "units");
        Directory.CreateDirectory(unitsDir);

        var packagePath = Path.Combine(tempRoot, "package.yaml");
        await File.WriteAllTextAsync(packagePath, packageYaml);

        foreach (var (filename, content) in unitFiles)
        {
            await File.WriteAllTextAsync(Path.Combine(unitsDir, filename), content);
        }

        return new TempPackage(tempRoot, packageYaml);
    }

    private sealed class TempPackage : System.IDisposable
    {
        public string PackageRoot { get; }
        public string PackageYaml { get; }

        public TempPackage(string packageRoot, string packageYaml)
        {
            PackageRoot = packageRoot;
            PackageYaml = packageYaml;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(PackageRoot))
                {
                    Directory.Delete(PackageRoot, recursive: true);
                }
            }
            catch { /* best effort */ }
        }
    }
}