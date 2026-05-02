// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="PackageManifestParser.ParseRaw"/> — the schema
/// parse layer without reference resolution.
/// </summary>
public class PackageManifestParserRawTests
{
    // ---- Happy-path parsing ---------------------------------------------

    [Fact]
    public void ParseRaw_MinimalUnitPackage_Succeeds()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: my-package
            unit: root-unit
            """;

        var manifest = PackageManifestParser.ParseRaw(yaml);

        manifest.Kind.ShouldBe("UnitPackage");
        manifest.Metadata.ShouldNotBeNull();
        manifest.Metadata!.Name.ShouldBe("my-package");
        manifest.Unit.ShouldBe("root-unit");
        manifest.Inputs.ShouldBeNull();
    }

    [Fact]
    public void ParseRaw_MinimalAgentPackage_Succeeds()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: AgentPackage
            metadata:
              name: agent-pkg
              description: An agent package.
            agent: my-agent
            """;

        var manifest = PackageManifestParser.ParseRaw(yaml);

        manifest.Kind.ShouldBe("AgentPackage");
        manifest.Metadata!.Name.ShouldBe("agent-pkg");
        manifest.Metadata.Description.ShouldBe("An agent package.");
        manifest.Agent.ShouldBe("my-agent");
    }

    [Fact]
    public void ParseRaw_FullUnitPackage_MapsAllFields()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: full-pkg
              description: Full package.
              displayName: Full Package
            inputs:
              - name: team_name
                type: string
                required: true
                description: The team name.
              - name: replica_count
                type: int
                required: false
                default: "1"
              - name: api_key
                type: string
                secret: true
                required: false
            unit: root-unit
            subUnits:
              - sub-unit-a
              - other-pkg/shared-unit
            skills:
              - code-review
            workflows:
              - ci-workflow
            """;

        var manifest = PackageManifestParser.ParseRaw(yaml);

        manifest.Kind.ShouldBe("UnitPackage");
        manifest.Metadata!.DisplayName.ShouldBe("Full Package");

        manifest.Inputs.ShouldNotBeNull();
        manifest.Inputs!.Count.ShouldBe(3);

        manifest.Inputs[0].Name.ShouldBe("team_name");
        manifest.Inputs[0].Type.ShouldBe("string");
        manifest.Inputs[0].Required.ShouldBeTrue();
        manifest.Inputs[0].Secret.ShouldBeFalse();

        manifest.Inputs[1].Name.ShouldBe("replica_count");
        manifest.Inputs[1].Type.ShouldBe("int");
        manifest.Inputs[1].Required.ShouldBeFalse();
        manifest.Inputs[1].Default.ShouldBe("1");

        manifest.Inputs[2].Name.ShouldBe("api_key");
        manifest.Inputs[2].Secret.ShouldBeTrue();

        manifest.Unit.ShouldBe("root-unit");
        manifest.SubUnits.ShouldNotBeNull();
        manifest.SubUnits!.Count.ShouldBe(2);
        manifest.SubUnits[0].ShouldBe("sub-unit-a");
        manifest.SubUnits[1].ShouldBe("other-pkg/shared-unit");

        manifest.Skills!.Count.ShouldBe(1);
        manifest.Skills[0].ShouldBe("code-review");

        manifest.Workflows!.Count.ShouldBe(1);
        manifest.Workflows[0].ShouldBe("ci-workflow");
    }

    // ---- Required-field failures ----------------------------------------

    [Fact]
    public void ParseRaw_MissingKind_Throws()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            metadata:
              name: pkg
            unit: root
            """;

        var act = () => PackageManifestParser.ParseRaw(yaml);

        Should.Throw<PackageParseException>(act)
            .Message.ShouldContain("kind");
    }

    [Fact]
    public void ParseRaw_UnknownKind_Throws()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: BadKind
            metadata:
              name: pkg
            """;

        var act = () => PackageManifestParser.ParseRaw(yaml);

        Should.Throw<PackageParseException>(act)
            .Message.ShouldContain("BadKind");
    }

    [Fact]
    public void ParseRaw_MissingMetadataName_Throws()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              description: no name here
            unit: root
            """;

        var act = () => PackageManifestParser.ParseRaw(yaml);

        Should.Throw<PackageParseException>(act)
            .Message.ShouldContain("metadata.name");
    }

    [Fact]
    public void ParseRaw_MissingMetadata_Throws()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            unit: root
            """;

        var act = () => PackageManifestParser.ParseRaw(yaml);

        Should.Throw<PackageParseException>(act)
            .Message.ShouldContain("metadata.name");
    }

    [Fact]
    public void ParseRaw_EmptyYaml_Throws()
    {
        var act = () => PackageManifestParser.ParseRaw("");

        Should.Throw<PackageParseException>(act);
    }

    [Fact]
    public void ParseRaw_InvalidYaml_Throws()
    {
        var yaml = "kind: [\nbroken: yaml: here";

        var act = () => PackageManifestParser.ParseRaw(yaml);

        Should.Throw<PackageParseException>(act)
            .Message.ShouldContain("YAML");
    }

    // ---- Backward compatibility: old single-unit YAML still parses via ManifestParser ----------

    [Fact]
    public void ManifestParser_OldSingleUnitYaml_StillParses()
    {
        // Acceptance criterion 11: an existing single-unit YAML (no apiVersion/kind)
        // still parses through UnitManifest directly without going through the new
        // package shape. The v0.1 transition keeps both shapes alive.
        var yaml = """
            unit:
              name: legacy-unit
              description: A legacy unit YAML without package wrapper.
              members:
                - agent: worker
            """;

        var manifest = ManifestParser.Parse(yaml);

        manifest.Name.ShouldBe("legacy-unit");
        manifest.Description.ShouldBe("A legacy unit YAML without package wrapper.");
        manifest.Members.ShouldNotBeNull();
        manifest.Members!.Count.ShouldBe(1);
        manifest.Members[0].Agent.ShouldBe("worker");
    }
}