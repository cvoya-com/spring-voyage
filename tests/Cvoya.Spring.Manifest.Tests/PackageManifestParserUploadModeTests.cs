// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="PackageManifestParser.ParseAndResolveAsync"/> when
/// <c>packageRoot</c> is <c>null</c> or empty (upload semantics, ADR-0035
/// decision 13). Self-contained packages must parse successfully; packages
/// with any bare local reference must throw
/// <see cref="PackageUploadHasLocalRefException"/> listing every offending ref.
/// </summary>
public class PackageManifestParserUploadModeTests
{
    // ---- Test 1: self-contained AgentPackage with null packageRoot --------

    [Fact]
    public async Task ParseAndResolve_NullPackageRoot_SelfContainedAgentPackage_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;

        // AgentPackage with a cross-package agent ref (no local refs) — the
        // agent comes from the catalog, so no packageRoot is needed.
        var catalogProvider = new StubCatalogProvider()
            .AddArtefact("spring-voyage-oss", ArtefactKind.Agent, "architect",
                "agent:\n  id: architect\n  name: Architect\n  role: architect");

        const string Yaml = """
            apiVersion: spring.voyage/v1
            kind: AgentPackage
            metadata:
              name: my-agent-pkg
            agent: spring-voyage-oss/architect
            """;

        var result = await PackageManifestParser.ParseAndResolveAsync(
            Yaml, packageRoot: null, catalogProvider: catalogProvider, cancellationToken: ct);

        result.Name.ShouldBe("my-agent-pkg");
        result.Kind.ShouldBe(PackageKind.AgentPackage);
        result.Agents.Count.ShouldBe(1);
        result.Agents[0].Name.ShouldBe("architect");
        result.Agents[0].IsCrossPackage.ShouldBeTrue();
    }

    // ---- Test 2: self-contained UnitPackage with no artefact refs ---------

    [Fact]
    public async Task ParseAndResolve_NullPackageRoot_NoRefs_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;

        const string Yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: minimal-pkg
              description: A package with no artefact references.
            """;

        var result = await PackageManifestParser.ParseAndResolveAsync(
            Yaml, packageRoot: null, cancellationToken: ct);

        result.Name.ShouldBe("minimal-pkg");
        result.Units.Count.ShouldBe(0);
        result.Agents.Count.ShouldBe(0);
        result.Skills.Count.ShouldBe(0);
        result.Workflows.Count.ShouldBe(0);
    }

    // ---- Test 3: UnitPackage with bare subUnit ref raises exception --------

    [Fact]
    public async Task ParseAndResolve_NullPackageRoot_LocalSubUnitRef_ThrowsUploadException()
    {
        var ct = TestContext.Current.CancellationToken;

        const string Yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: multi-file-pkg
            unit: root-unit
            subUnits:
              - child-unit
            """;

        var ex = await Should.ThrowAsync<PackageUploadHasLocalRefException>(
            () => PackageManifestParser.ParseAndResolveAsync(
                Yaml, packageRoot: null, cancellationToken: ct));

        // Both local refs must be listed.
        ex.LocalReferences.Count.ShouldBe(2);
        ex.LocalReferences.ShouldContain(r => r.Contains("root-unit"));
        ex.LocalReferences.ShouldContain(r => r.Contains("child-unit"));
        ex.Message.ShouldContain("local references");
        ex.Message.ShouldContain("self-contained");
    }

    // ---- Test 4: single bare unit ref raises exception with that ref ------

    [Fact]
    public async Task ParseAndResolve_NullPackageRoot_SingleLocalUnitRef_ThrowsWithThatRef()
    {
        var ct = TestContext.Current.CancellationToken;

        const string Yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: needs-local-unit
            unit: my-local-unit
            """;

        var ex = await Should.ThrowAsync<PackageUploadHasLocalRefException>(
            () => PackageManifestParser.ParseAndResolveAsync(
                Yaml, packageRoot: null, cancellationToken: ct));

        ex.LocalReferences.Count.ShouldBe(1);
        ex.LocalReferences[0].ShouldContain("my-local-unit");
    }

    // ---- Test 5: multiple local refs across kinds all reported at once -----

    [Fact]
    public async Task ParseAndResolve_NullPackageRoot_MultipleLocalRefsAcrossKinds_AllListed()
    {
        var ct = TestContext.Current.CancellationToken;

        const string Yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: multi-kind-pkg
            unit: my-unit
            skills:
              - my-skill
            """;

        var ex = await Should.ThrowAsync<PackageUploadHasLocalRefException>(
            () => PackageManifestParser.ParseAndResolveAsync(
                Yaml, packageRoot: null, cancellationToken: ct));

        ex.LocalReferences.Count.ShouldBe(2);
        ex.LocalReferences.ShouldContain(r => r.Contains("my-unit"));
        ex.LocalReferences.ShouldContain(r => r.Contains("my-skill"));
    }

    // ---- Test 6: empty-string packageRoot behaves identically to null -----

    [Fact]
    public async Task ParseAndResolve_EmptyPackageRoot_LocalRef_ThrowsSameAsNull()
    {
        var ct = TestContext.Current.CancellationToken;

        const string Yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: empty-root-pkg
            unit: local-unit
            """;

        var exNull = await Should.ThrowAsync<PackageUploadHasLocalRefException>(
            () => PackageManifestParser.ParseAndResolveAsync(
                Yaml, packageRoot: null, cancellationToken: ct));

        var exEmpty = await Should.ThrowAsync<PackageUploadHasLocalRefException>(
            () => PackageManifestParser.ParseAndResolveAsync(
                Yaml, packageRoot: string.Empty, cancellationToken: ct));

        exNull.LocalReferences.Count.ShouldBe(exEmpty.LocalReferences.Count);
        exNull.LocalReferences[0].ShouldBe(exEmpty.LocalReferences[0]);
    }

    // ---- Test 7: cross-package ref with null packageRoot resolves via catalog

    [Fact]
    public async Task ParseAndResolve_NullPackageRoot_CrossPackageRef_ResolvesViaCatalog()
    {
        var ct = TestContext.Current.CancellationToken;

        var catalogProvider = new StubCatalogProvider()
            .AddArtefact("shared-pkg", ArtefactKind.Unit, "shared-unit",
                "unit:\n  name: shared-unit\n  description: Cross-package unit.");

        const string Yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: consumer-pkg
            unit: shared-pkg/shared-unit
            """;

        var result = await PackageManifestParser.ParseAndResolveAsync(
            Yaml, packageRoot: null, catalogProvider: catalogProvider, cancellationToken: ct);

        result.Units.Count.ShouldBe(1);
        result.Units[0].Name.ShouldBe("shared-unit");
        result.Units[0].SourcePackage.ShouldBe("shared-pkg");
        result.Units[0].IsCrossPackage.ShouldBeTrue();
    }

    // ---- Test 8: exception inherits from PackageParseException (400 mapping)

    [Fact]
    public async Task PackageUploadHasLocalRefException_InheritsFromPackageParseException()
    {
        var ct = TestContext.Current.CancellationToken;

        const string Yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: local-pkg
            unit: some-unit
            """;

        var ex = await Should.ThrowAsync<PackageUploadHasLocalRefException>(
            () => PackageManifestParser.ParseAndResolveAsync(
                Yaml, packageRoot: null, cancellationToken: ct));

        // Must be catch-able as PackageParseException so existing 400 mapping works.
        ex.ShouldBeAssignableTo<PackageParseException>();
    }

    // ---- Stub catalog provider ------------------------------------------

    private sealed class StubCatalogProvider : IPackageCatalogProvider
    {
        private readonly System.Collections.Generic.HashSet<string> _existingPackages =
            new(System.StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Generic.Dictionary<string, string> _artefacts =
            new(System.StringComparer.OrdinalIgnoreCase);

        public StubCatalogProvider AddArtefact(
            string packageName, ArtefactKind kind, string artefactName, string content)
        {
            _existingPackages.Add(packageName);
            _artefacts[$"{packageName}|{kind}|{artefactName}"] = content;
            return this;
        }

        public Task<bool> PackageExistsAsync(
            string packageName, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(_existingPackages.Contains(packageName));

        public Task<string?> LoadArtefactYamlAsync(
            string packageName, ArtefactKind kind, string artefactName,
            System.Threading.CancellationToken cancellationToken = default)
        {
            var key = $"{packageName}|{kind}|{artefactName}";
            return Task.FromResult(_artefacts.TryGetValue(key, out var v) ? v : (string?)null);
        }
    }
}