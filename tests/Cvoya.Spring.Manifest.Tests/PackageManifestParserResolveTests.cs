// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

/// <summary>
/// Integration-level tests for <see cref="PackageManifestParser.ParseAndResolveAsync"/>:
/// within-package reference resolution, cross-package resolution, cycle
/// detection, name uniqueness, and round-trip semantics.
/// </summary>
public class PackageManifestParserResolveTests
{
    private static string FixturePackageRoot(string packageName)
    {
        // Fixtures are copied to output alongside the test assembly.
        var baseDir = AppContext.BaseDirectory;
        return Path.Combine(baseDir, "Fixtures", "Packages", packageName);
    }

    // ---- Test 1: Bare references resolve (all four artefact types) ------

    [Fact]
    public async Task ParseAndResolveAsync_SimpleUnitPackage_AllFourArtefactTypesResolved()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = FixturePackageRoot("simple-unit-package");
        var yaml = await File.ReadAllTextAsync(Path.Combine(root, "package.yaml"), ct);

        var result = await PackageManifestParser.ParseAndResolveAsync(
            yaml, root, cancellationToken: ct);

        result.Name.ShouldBe("simple-unit-package");
        result.Kind.ShouldBe(PackageKind.UnitPackage);

        // Units: root-unit + sub-unit
        result.Units.Count.ShouldBe(2);
        result.Units.ShouldContain(u => u.Name == "root-unit");
        result.Units.ShouldContain(u => u.Name == "sub-unit");
        result.Units.ShouldAllBe(u => !u.IsCrossPackage);
        result.Units.ShouldAllBe(u => u.Content != null);
        result.Units.ShouldAllBe(u => u.ResolvedPath != null);

        // Skills: my-skill
        result.Skills.Count.ShouldBe(1);
        result.Skills[0].Name.ShouldBe("my-skill");
        result.Skills[0].Content.ShouldNotBeNull();

        // Workflows: my-workflow (directory, no content)
        result.Workflows.Count.ShouldBe(1);
        result.Workflows[0].Name.ShouldBe("my-workflow");
        result.Workflows[0].ResolvedPath.ShouldNotBeNull();

        // No agents in this package.
        result.Agents.Count.ShouldBe(0);
    }

    [Fact]
    public async Task ParseAndResolveAsync_AgentPackage_RootAgentResolved()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = FixturePackageRoot("simple-agent-package");
        var yaml = await File.ReadAllTextAsync(Path.Combine(root, "package.yaml"), ct);

        var result = await PackageManifestParser.ParseAndResolveAsync(
            yaml, root, cancellationToken: ct);

        result.Name.ShouldBe("simple-agent-package");
        result.Kind.ShouldBe(PackageKind.AgentPackage);
        result.Agents.Count.ShouldBe(1);
        result.Agents[0].Name.ShouldBe("my-agent");
        result.Agents[0].Content.ShouldNotBeNull();
        result.Agents[0].Content!.ShouldContain("my-agent");
    }

    // ---- Test 2: Cross-package references resolve ----------------------

    [Fact]
    public async Task ParseAndResolveAsync_CrossPackageUnit_ResolvedViaCatalog()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalogProvider = new StubCatalogProvider()
            .AddArtefact("other-pkg", ArtefactKind.Unit, "shared-unit",
                "unit:\n  name: shared-unit\n  description: Cross-package unit.");

        var yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: consumer-pkg
            unit: other-pkg/shared-unit
            """;

        var result = await PackageManifestParser.ParseAndResolveAsync(
            yaml, "/tmp/fake-root", catalogProvider: catalogProvider,
            cancellationToken: ct);

        result.Units.Count.ShouldBe(1);
        result.Units[0].Name.ShouldBe("shared-unit");
        result.Units[0].SourcePackage.ShouldBe("other-pkg");
        result.Units[0].IsCrossPackage.ShouldBeTrue();
        result.Units[0].Content.ShouldNotBeNull();
        result.Units[0].Content!.ShouldContain("shared-unit");
    }

    [Fact]
    public async Task ParseAndResolveAsync_CrossPackageAgent_ResolvedViaCatalog()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalogProvider = new StubCatalogProvider()
            .AddArtefact("spring-voyage-oss", ArtefactKind.Agent, "architect",
                "agent:\n  id: architect\n  name: Architect\n  role: architect");

        using var tmpDir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tmpDir.Path, "units"));
        await File.WriteAllTextAsync(
            Path.Combine(tmpDir.Path, "units", "root.yaml"),
            "unit:\n  name: root\n", ct);

        var agentPackageYaml = """
            apiVersion: spring.voyage/v1
            kind: AgentPackage
            metadata:
              name: consumer-pkg
            agent: spring-voyage-oss/architect
            """;

        var result = await PackageManifestParser.ParseAndResolveAsync(
            agentPackageYaml, tmpDir.Path, catalogProvider: catalogProvider,
            cancellationToken: ct);

        result.Agents.Count.ShouldBe(1);
        result.Agents[0].Name.ShouldBe("architect");
        result.Agents[0].SourcePackage.ShouldBe("spring-voyage-oss");
    }

    [Fact]
    public async Task ParseAndResolveAsync_CrossPackageSkill_ResolvedViaCatalog()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalogProvider = new StubCatalogProvider()
            .AddArtefact("research-pkg", ArtefactKind.Skill, "literature-review",
                "# Literature Review\n\nSearch for academic papers on a topic.");

        using var tmpDir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tmpDir.Path, "units"));
        await File.WriteAllTextAsync(
            Path.Combine(tmpDir.Path, "units", "root.yaml"),
            "unit:\n  name: root\n", ct);

        var yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: consumer-pkg
            unit: root
            skills:
              - research-pkg/literature-review
            """;

        var result = await PackageManifestParser.ParseAndResolveAsync(
            yaml, tmpDir.Path, catalogProvider: catalogProvider,
            cancellationToken: ct);

        result.Skills.Count.ShouldBe(1);
        result.Skills[0].Name.ShouldBe("literature-review");
        result.Skills[0].SourcePackage.ShouldBe("research-pkg");
    }

    [Fact]
    public async Task ParseAndResolveAsync_CrossPackageWorkflow_ResolvedViaCatalog()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalogProvider = new StubCatalogProvider()
            .AddArtefact("analytics-pkg", ArtefactKind.Workflow, "ci-pipeline",
                "name: ci-pipeline\nsteps: []");

        using var tmpDir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tmpDir.Path, "units"));
        await File.WriteAllTextAsync(
            Path.Combine(tmpDir.Path, "units", "root.yaml"),
            "unit:\n  name: root\n", ct);

        var yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: consumer-pkg
            unit: root
            workflows:
              - analytics-pkg/ci-pipeline
            """;

        var result = await PackageManifestParser.ParseAndResolveAsync(
            yaml, tmpDir.Path, catalogProvider: catalogProvider,
            cancellationToken: ct);

        result.Workflows.Count.ShouldBe(1);
        result.Workflows[0].Name.ShouldBe("ci-pipeline");
        result.Workflows[0].SourcePackage.ShouldBe("analytics-pkg");
    }

    // ---- Test 3: Cross-package reference to a missing package ----------

    [Fact]
    public async Task ParseAndResolveAsync_MissingPackage_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalogProvider = new StubCatalogProvider(); // empty catalog.

        var yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: consumer-pkg
            unit: missing-pkg/some-unit
            """;

        var ex = await Should.ThrowAsync<PackageReferenceNotFoundException>(
            () => PackageManifestParser.ParseAndResolveAsync(
                yaml, "/tmp/fake", catalogProvider: catalogProvider,
                cancellationToken: ct));

        ex.Reference.ShouldBe("missing-pkg/some-unit");
        ex.Message.ShouldContain("missing-pkg");
    }

    // ---- Test 4: Cross-package reference to missing artefact in known package ----

    [Fact]
    public async Task ParseAndResolveAsync_MissingArtefactInKnownPackage_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        // Package exists but the specific artefact does not.
        var catalogProvider = new StubCatalogProvider()
            .MarkPackageExists("known-pkg"); // package exists but no artefacts registered.

        var yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: consumer-pkg
            unit: known-pkg/nonexistent-unit
            """;

        var ex = await Should.ThrowAsync<PackageReferenceNotFoundException>(
            () => PackageManifestParser.ParseAndResolveAsync(
                yaml, "/tmp/fake", catalogProvider: catalogProvider,
                cancellationToken: ct));

        ex.Reference.ShouldBe("known-pkg/nonexistent-unit");
        ex.Message.ShouldContain("nonexistent-unit");
        ex.Message.ShouldContain("known-pkg");
    }

    // ---- Test 5: Cycle detection ----------------------------------------

    [Fact]
    public async Task ParseAndResolveAsync_CycleDetected_ThrowsWithCyclePath()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = FixturePackageRoot("cycle-package");
        var yaml = await File.ReadAllTextAsync(Path.Combine(root, "package.yaml"), ct);

        var ex = await Should.ThrowAsync<PackageCycleException>(
            () => PackageManifestParser.ParseAndResolveAsync(
                yaml, root, cancellationToken: ct));

        ex.CyclePath.ShouldNotBeEmpty();
        // The cycle is unit-a → unit-b → unit-c → unit-a.
        ex.CyclePath.Count.ShouldBe(3);
        var cycleNames = ex.CyclePath.ToList();
        cycleNames.ShouldContain("unit-a");
        cycleNames.ShouldContain("unit-b");
        cycleNames.ShouldContain("unit-c");
    }

    // ---- Test 6: Required input missing ---------------------------------

    [Fact]
    public async Task ParseAndResolveAsync_RequiredInputMissing_ThrowsActionableError()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = FixturePackageRoot("input-package");
        var yaml = await File.ReadAllTextAsync(Path.Combine(root, "package.yaml"), ct);

        // Supply only team_name; package_name is also required.
        var values = new Dictionary<string, string> { ["team_name"] = "Engineering" };

        var ex = await Should.ThrowAsync<PackageInputValidationException>(
            () => PackageManifestParser.ParseAndResolveAsync(
                yaml, root, values, cancellationToken: ct));

        ex.InputName.ShouldBe("package_name");
        ex.Message.ShouldContain("package_name");
        ex.Message.ShouldContain("required");
    }

    // ---- Test 7: Type mismatch ------------------------------------------

    [Fact]
    public async Task ParseAndResolveAsync_IntInputTypeMismatch_ThrowsActionableError()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = FixturePackageRoot("input-package");
        var yaml = await File.ReadAllTextAsync(Path.Combine(root, "package.yaml"), ct);

        var values = new Dictionary<string, string>
        {
            ["package_name"] = "my-pkg",
            ["team_name"] = "Engineering",
            ["replica_count"] = "not-a-number",   // wrong type
        };

        var ex = await Should.ThrowAsync<PackageInputValidationException>(
            () => PackageManifestParser.ParseAndResolveAsync(
                yaml, root, values, cancellationToken: ct));

        ex.InputName.ShouldBe("replica_count");
        ex.Message.ShouldContain("int");
        ex.Message.ShouldContain("not-a-number");
    }

    // ---- Test 8: Secret input -------------------------------------------

    [Fact]
    public async Task ParseAndResolveAsync_SecretInput_StoredAsSecretReference()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = FixturePackageRoot("input-package");
        var yaml = await File.ReadAllTextAsync(Path.Combine(root, "package.yaml"), ct);

        var values = new Dictionary<string, string>
        {
            ["package_name"] = "my-pkg",
            ["team_name"] = "Engineering",
            ["api_key"] = "secret://my-tenant/api-key",
        };

        var result = await PackageManifestParser.ParseAndResolveAsync(
            yaml, root, values, cancellationToken: ct);

        result.InputValues.ContainsKey("api_key").ShouldBeTrue();
        result.InputValues["api_key"].ShouldBe("secret://my-tenant/api-key");
    }

    [Fact]
    public async Task ParseAndResolveAsync_SecretInput_PlainValueWrappedAsSecretRef()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = FixturePackageRoot("input-package");
        var yaml = await File.ReadAllTextAsync(Path.Combine(root, "package.yaml"), ct);

        var values = new Dictionary<string, string>
        {
            ["package_name"] = "my-pkg",
            ["team_name"] = "Engineering",
            ["api_key"] = "plaintext-value",   // will be wrapped.
        };

        var result = await PackageManifestParser.ParseAndResolveAsync(
            yaml, root, values, cancellationToken: ct);

        result.InputValues["api_key"].ShouldBe("secret://plaintext-value");
    }

    // ---- Test 9: Round-trip fidelity ------------------------------------

    [Fact]
    public void ParseRaw_ParseSerializeParseRaw_SemanticFieldsMatch()
    {
        // ADR-0035 decision 12: round-trip via the parsed object graph (not
        // the YAML blob — comment fidelity is the install layer's job).
        // We verify that the semantically load-bearing fields survive a
        // parse → re-emit → re-parse cycle.
        var original = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: round-trip-pkg
              description: A test package.
            inputs:
              - name: team_name
                type: string
                required: true
            unit: root-unit
            subUnits:
              - sub-unit
            skills:
              - my-skill
            """;

        var firstParse = PackageManifestParser.ParseRaw(original);

        // Re-emit as YAML using YamlDotNet's serializer.
        var serializer = new YamlDotNet.Serialization.SerializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .Build();
        var emitted = serializer.Serialize(firstParse);

        var secondParse = PackageManifestParser.ParseRaw(emitted);

        secondParse.Kind.ShouldBe(firstParse.Kind);
        secondParse.Metadata!.Name.ShouldBe(firstParse.Metadata!.Name);
        secondParse.Metadata.Description.ShouldBe(firstParse.Metadata.Description);
        secondParse.Unit.ShouldBe(firstParse.Unit);
        secondParse.SubUnits.ShouldNotBeNull();
        secondParse.SubUnits!.Count.ShouldBe(firstParse.SubUnits!.Count);
        secondParse.Skills!.Count.ShouldBe(firstParse.Skills!.Count);
        secondParse.Inputs!.Count.ShouldBe(firstParse.Inputs!.Count);
        secondParse.Inputs[0].Name.ShouldBe(firstParse.Inputs[0].Name);
        secondParse.Inputs[0].Required.ShouldBe(firstParse.Inputs[0].Required);
    }

    // ---- Test 10: Name uniqueness ---------------------------------------

    [Fact]
    public async Task ParseAndResolveAsync_DuplicateSubUnitName_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tmpDir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tmpDir.Path, "units"));

        // unit-a appears twice — as root unit and in subUnits.
        await File.WriteAllTextAsync(
            Path.Combine(tmpDir.Path, "units", "unit-a.yaml"),
            "unit:\n  name: unit-a\n", ct);

        var yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: collision-pkg
            unit: unit-a
            subUnits:
              - unit-a
            """;

        var ex = await Should.ThrowAsync<PackageParseException>(
            () => PackageManifestParser.ParseAndResolveAsync(
                yaml, tmpDir.Path, cancellationToken: ct));

        ex.Message.ShouldContain("unit-a");
        ex.Message.ShouldContain("Duplicate");
    }

    // ---- Test 11: Backward compat — old unit YAML parsed via ManifestParser ----
    // (Covered in PackageManifestParserRawTests.ManifestParser_OldSingleUnitYaml_StillParses)

    // ---- Stub catalog provider ------------------------------------------

    private sealed class StubCatalogProvider : IPackageCatalogProvider
    {
        private readonly HashSet<string> _existingPackages = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _artefacts =
            new(StringComparer.OrdinalIgnoreCase);

        public StubCatalogProvider AddArtefact(
            string packageName, ArtefactKind kind, string artefactName, string content)
        {
            _existingPackages.Add(packageName);
            _artefacts[$"{packageName}|{kind}|{artefactName}"] = content;
            return this;
        }

        public StubCatalogProvider MarkPackageExists(string packageName)
        {
            _existingPackages.Add(packageName);
            return this;
        }

        public Task<bool> PackageExistsAsync(string packageName, CancellationToken cancellationToken = default)
            => Task.FromResult(_existingPackages.Contains(packageName));

        public Task<string?> LoadArtefactYamlAsync(
            string packageName, ArtefactKind kind, string artefactName,
            CancellationToken cancellationToken = default)
        {
            var key = $"{packageName}|{kind}|{artefactName}";
            return Task.FromResult(_artefacts.TryGetValue(key, out var v) ? v : (string?)null);
        }
    }

    /// <summary>A temp directory that deletes itself on dispose.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "sv-manifest-tests-" + Guid.NewGuid().ToString("N")[..8]);

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best effort */ }
        }
    }
}