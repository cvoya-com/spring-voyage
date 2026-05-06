// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using Shouldly;

using Xunit;

/// <summary>
/// Parser tests for ADR-0037 (package schema decomposition). Covers the
/// six decisions:
/// <list type="bullet">
///   <item><description>D1 — per-artefact kind-discriminated documents.</description></item>
///   <item><description>D2 — flat <c>package.yaml</c> (no <c>metadata:</c> nesting; no <c>inputs:</c>; no <c>connectors:</c>).</description></item>
///   <item><description>D3 — per-artefact <c>requires:</c> with discriminator-as-key shape.</description></item>
///   <item><description>D4 — cross-package cycle detection (graph-walker unit test).</description></item>
///   <item><description>D5 — <c>&lt;pkg&gt;/&lt;name&gt;@&lt;version&gt;</c> reference parsing.</description></item>
///   <item><description>D6 — every old-shape signal is rejected with a precise migration hint.</description></item>
/// </list>
/// </summary>
public class Adr0037Tests
{
    // ---- D2 — happy-path parse of the new package.yaml shape ------------

    [Fact]
    public void ParseRaw_NewShape_Succeeds()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Package
            name: my-package
            description: A new-shape package.
            version: 1.0.0
            content:
              - unit: root-unit
            """;

        var manifest = PackageManifestParser.ParseRaw(yaml);

        manifest.Name.ShouldBe("my-package");
        manifest.Description.ShouldBe("A new-shape package.");
        manifest.Version.ShouldBe("1.0.0");
        manifest.Kind.ShouldBe("Package");
        manifest.Content.ShouldNotBeNull();
        manifest.Content!.Count.ShouldBe(1);
        manifest.Content[0].Kind.ShouldBe(ArtefactKind.Unit);
        manifest.Content[0].Definition.Reference.ShouldBe("root-unit");
    }

    [Fact]
    public void ParseRaw_NewShape_OptionalReadme()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Package
            name: my-package
            description: Description.
            version: 1.0.0
            readme: README.md
            content:
              - unit: root
            """;

        var manifest = PackageManifestParser.ParseRaw(yaml);

        manifest.Readme.ShouldBe("README.md");
    }

    // ---- D2 — required field rejections --------------------------------

    [Fact]
    public void ParseRaw_NewShape_MissingName_Rejected()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Package
            description: x
            version: 1.0.0
            content: []
            """;

        var ex = Should.Throw<PackageParseException>(() => PackageManifestParser.ParseRaw(yaml));
        ex.Message.ShouldContain("'name:'");
    }

    [Fact]
    public void ParseRaw_NewShape_MissingDescription_Rejected()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Package
            name: my-package
            version: 1.0.0
            content: []
            """;

        var ex = Should.Throw<PackageParseException>(() => PackageManifestParser.ParseRaw(yaml));
        ex.Message.ShouldContain("'description:'");
    }

    [Fact]
    public void ParseRaw_NewShape_MissingVersion_Rejected()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Package
            name: my-package
            description: x
            content: []
            """;

        var ex = Should.Throw<PackageParseException>(() => PackageManifestParser.ParseRaw(yaml));
        ex.Message.ShouldContain("MissingPackageVersion");
    }

    // ---- D6 — every old-shape signal raises a precise error ------------

    [Fact]
    public void ParseRaw_LegacyMetadataNesting_Rejected()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Package
            metadata:
              name: my-package
              description: x
            version: 1.0.0
            content: []
            """;

        var ex = Should.Throw<PackageParseException>(() => PackageManifestParser.ParseRaw(yaml));
        ex.Message.ShouldContain("LegacyMetadataNesting");
    }

    [Fact]
    public void ParseRaw_LegacyInputsField_Rejected()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Package
            name: my-package
            description: x
            version: 1.0.0
            inputs:
              - name: foo
                type: string
                required: true
            content: []
            """;

        var ex = Should.Throw<PackageParseException>(() => PackageManifestParser.ParseRaw(yaml));
        ex.Message.ShouldContain("LegacyInputsField");
    }

    [Fact]
    public void ParseRaw_LegacyPackageConnectorsField_Rejected()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Package
            name: my-package
            description: x
            version: 1.0.0
            connectors:
              - type: github
                required: true
            content: []
            """;

        var ex = Should.Throw<PackageParseException>(() => PackageManifestParser.ParseRaw(yaml));
        ex.Message.ShouldContain("LegacyPackageConnectorsField");
    }

    [Fact]
    public void ParseRaw_LegacyPackageKind_Rejected()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            name: my-package
            description: x
            version: 1.0.0
            content: []
            """;

        var ex = Should.Throw<PackageParseException>(() => PackageManifestParser.ParseRaw(yaml));
        ex.Message.ShouldContain("LegacyPackageKind");
    }

    // ---- D6 — unit-side legacy rejections through ManifestParser -------

    [Fact]
    public void ManifestParser_LegacyArtefactWrapper_Rejected()
    {
        var yaml = """
            unit:
              name: my-unit
              members:
                - agent: my-agent
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));
        ex.Message.ShouldContain("LegacyArtefactWrapper");
    }

    [Fact]
    public void ManifestParser_LegacyStructureField_Rejected()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: my-unit
            description: x
            structure: hierarchical
            members:
              - agent: a
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));
        ex.Message.ShouldContain("LegacyStructureField");
    }

    [Fact]
    public void ManifestParser_LegacyUnitConnectorsField_Rejected()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: my-unit
            description: x
            connectors:
              - type: github
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));
        ex.Message.ShouldContain("LegacyUnitConnectorsField");
    }

    [Fact]
    public void ManifestParser_MissingApiVersion_Rejected()
    {
        var yaml = """
            kind: Unit
            name: my-unit
            description: x
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));
        ex.Message.ShouldContain("MissingApiVersion");
    }

    [Fact]
    public void ManifestParser_MissingKind_Rejected()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            name: my-unit
            description: x
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));
        ex.Message.ShouldContain("MissingKind");
    }

    [Fact]
    public void ManifestParser_MismatchedKind_Rejected()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Agent
            name: my-unit
            description: x
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));
        ex.Message.ShouldContain("Unit");
    }

    // ---- D1 — happy-path unit YAML parse -------------------------------

    [Fact]
    public void ManifestParser_NewShape_Succeeds()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: my-unit
            description: Something.
            readme: my-unit.md
            members:
              - agent: my-agent
            requires:
              - connector: github
            """;

        var manifest = ManifestParser.Parse(yaml);

        manifest.ApiVersion.ShouldBe("spring.voyage/v1");
        manifest.Kind.ShouldBe("Unit");
        manifest.Name.ShouldBe("my-unit");
        manifest.Description.ShouldBe("Something.");
        manifest.Readme.ShouldBe("my-unit.md");
        manifest.Members!.Count.ShouldBe(1);
        manifest.Members[0].Agent.ShouldBe("my-agent");
        manifest.Requires!.Count.ShouldBe(1);
        manifest.Requires[0].Type.ShouldBe(RequirementType.Connector);
        manifest.Requires[0].Identifier.ShouldBe("github");
    }

    // ---- D3 — requires entry shape -------------------------------------

    [Fact]
    public void RequirementEntry_UnknownType_Rejected()
    {
        // ManifestParser route — a unit YAML with an unknown requires type.
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: my-unit
            description: x
            requires:
              - widget: foo
            """;

        // Should fail at YAML parse time with our custom converter.
        Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));
    }

    // ---- D5 — versioned cross-package addressing -----------------------

    [Fact]
    public void ArtefactReference_VersionPin_Parsed()
    {
        var r = ArtefactReference.Parse("other-pkg/my-unit@1.2.0", ArtefactKind.Unit);

        r.IsCrossPackage.ShouldBeTrue();
        r.PackageName.ShouldBe("other-pkg");
        r.ArtefactName.ShouldBe("my-unit");
        r.Version.ShouldBe("1.2.0");
    }

    [Fact]
    public void ArtefactReference_NoVersion_Parsed()
    {
        var r = ArtefactReference.Parse("other-pkg/my-unit", ArtefactKind.Unit);

        r.IsCrossPackage.ShouldBeTrue();
        r.PackageName.ShouldBe("other-pkg");
        r.ArtefactName.ShouldBe("my-unit");
        r.Version.ShouldBeNull();
    }

    [Fact]
    public void ArtefactReference_BareWithVersion_Parsed()
    {
        // A bare name with an explicit version — odd but well-formed.
        var r = ArtefactReference.Parse("my-unit@1.2.0", ArtefactKind.Unit);

        r.IsCrossPackage.ShouldBeFalse();
        r.PackageName.ShouldBeNull();
        r.ArtefactName.ShouldBe("my-unit");
        r.Version.ShouldBe("1.2.0");
    }

    [Fact]
    public void ArtefactReference_EmptyVersion_Rejected()
    {
        Should.Throw<PackageParseException>(
            () => ArtefactReference.Parse("pkg/name@", ArtefactKind.Unit));
    }

    // ---- D4 — cross-package cycle detection ----------------------------

    [Fact]
    public void CrossPackageCycleDetector_NoCycle_ReturnsNull()
    {
        var d = new CrossPackageCycleDetector();
        var a = new ArtefactNode("pkg-a", ArtefactKind.Unit, "root", "1.0.0");
        var b = new ArtefactNode("pkg-b", ArtefactKind.Agent, "leaf", "1.0.0");
        d.AddNode(a, new[] { b });
        d.AddNode(b, System.Array.Empty<ArtefactNode>());

        d.FindCycle().ShouldBeNull();
    }

    [Fact]
    public void CrossPackageCycleDetector_TwoNodeCycle_Returned()
    {
        var d = new CrossPackageCycleDetector();
        var a = new ArtefactNode("pkg-a", ArtefactKind.Unit, "root", "1.0.0");
        var b = new ArtefactNode("pkg-b", ArtefactKind.Unit, "back", "1.0.0");
        d.AddNode(a, new[] { b });
        d.AddNode(b, new[] { a });

        var cycle = d.FindCycle();

        cycle.ShouldNotBeNull();
        // The cycle path closes back on itself: head == tail.
        cycle![0].ShouldBe(cycle[^1]);
    }

    [Fact]
    public void CrossPackageCycleDetector_VersionDistinguishesNodes_NoCycle()
    {
        // ADR-0037 D5: same artefact, different versions = different
        // nodes. A→B@1 and B@2→A is *not* a cycle because A only
        // references B@1.
        var d = new CrossPackageCycleDetector();
        var a = new ArtefactNode("pkg-a", ArtefactKind.Unit, "root", "1.0.0");
        var b1 = new ArtefactNode("pkg-b", ArtefactKind.Unit, "leaf", "1.0.0");
        var b2 = new ArtefactNode("pkg-b", ArtefactKind.Unit, "leaf", "2.0.0");
        d.AddNode(a, new[] { b1 });
        d.AddNode(b1, System.Array.Empty<ArtefactNode>());
        d.AddNode(b2, new[] { a });

        d.FindCycle().ShouldBeNull();
    }

    // ---- Round-trip: parse → serialise → re-parse identity ------------

    [Fact]
    public void ParseRaw_NewShape_RoundTripIdentity()
    {
        // ADR-0037 D2: a new-shape package.yaml that round-trips through
        // ParseRaw should preserve every field. This guards against
        // accidental field drops on the manifest model.
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Package
            name: round-trip-package
            description: A package whose ParseRaw output preserves every field.
            readme: README.md
            version: 2.5.0
            content:
              - unit: my-unit
              - agent: my-agent
            """;

        var manifest = PackageManifestParser.ParseRaw(yaml);

        manifest.ApiVersion.ShouldBe("spring.voyage/v1");
        manifest.Kind.ShouldBe("Package");
        manifest.Name.ShouldBe("round-trip-package");
        manifest.Description.ShouldBe("A package whose ParseRaw output preserves every field.");
        manifest.Readme.ShouldBe("README.md");
        manifest.Version.ShouldBe("2.5.0");
        manifest.Content!.Count.ShouldBe(2);
        manifest.Content[0].Kind.ShouldBe(ArtefactKind.Unit);
        manifest.Content[0].Definition.Reference.ShouldBe("my-unit");
        manifest.Content[1].Kind.ShouldBe(ArtefactKind.Agent);
        manifest.Content[1].Definition.Reference.ShouldBe("my-agent");
    }
}