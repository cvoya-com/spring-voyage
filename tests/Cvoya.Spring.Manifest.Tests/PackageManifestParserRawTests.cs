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

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public void ParseRaw_MinimalUnitPackage_Succeeds()
    {
        // #1718 item 1: no `kind:` field — package kind is inferred at
        // resolve time from the content list. Item 2: bundled artefacts
        // declared under `content:` instead of flat `unit:` / `subUnits:` /
        // `skills:` / `workflows:` lists.
        var yaml = """
            apiVersion: spring.voyage/v1
            metadata:
              name: my-package
            content:
              - unit: root-unit
            """;

        var manifest = PackageManifestParser.ParseRaw(yaml);

        manifest.Metadata.ShouldNotBeNull();
        manifest.Metadata!.Name.ShouldBe("my-package");
        manifest.Content.ShouldNotBeNull();
        manifest.Content!.Count.ShouldBe(1);
        manifest.Content[0].Kind.ShouldBe(ArtefactKind.Unit);
        manifest.Content[0].Definition.IsInline.ShouldBeFalse();
        manifest.Content[0].Definition.Reference.ShouldBe("root-unit");
        manifest.Inputs.ShouldBeNull();
    }

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public void ParseRaw_MinimalAgentPackage_Succeeds()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            metadata:
              name: agent-pkg
              description: An agent package.
            content:
              - agent: my-agent
            """;

        var manifest = PackageManifestParser.ParseRaw(yaml);

        manifest.Metadata!.Name.ShouldBe("agent-pkg");
        manifest.Metadata.Description.ShouldBe("An agent package.");
        manifest.Content.ShouldNotBeNull();
        manifest.Content!.Count.ShouldBe(1);
        manifest.Content[0].Kind.ShouldBe(ArtefactKind.Agent);
        manifest.Content[0].Definition.IsInline.ShouldBeFalse();
        manifest.Content[0].Definition.Reference.ShouldBe("my-agent");
    }

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public void ParseRaw_FullUnitPackage_MapsAllFields()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
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
            content:
              - unit: root-unit
              - unit: sub-unit-a
              - unit: other-pkg/shared-unit
              - skill: code-review
              - workflow: ci-workflow
            """;

        var manifest = PackageManifestParser.ParseRaw(yaml);

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

        manifest.Content.ShouldNotBeNull();
        manifest.Content!.Count.ShouldBe(5);
        manifest.Content[0].Kind.ShouldBe(ArtefactKind.Unit);
        manifest.Content[0].Definition.Reference.ShouldBe("root-unit");
        manifest.Content[1].Kind.ShouldBe(ArtefactKind.Unit);
        manifest.Content[1].Definition.Reference.ShouldBe("sub-unit-a");
        manifest.Content[2].Kind.ShouldBe(ArtefactKind.Unit);
        manifest.Content[2].Definition.Reference.ShouldBe("other-pkg/shared-unit");
        manifest.Content[3].Kind.ShouldBe(ArtefactKind.Skill);
        manifest.Content[3].Definition.Reference.ShouldBe("code-review");
        manifest.Content[4].Kind.ShouldBe(ArtefactKind.Workflow);
        manifest.Content[4].Definition.Reference.ShouldBe("ci-workflow");
    }

    // ---- #1718 item 1: kind: removed ------------------------------------

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public void ParseRaw_NoKindField_PackageStillParses()
    {
        // The defining acceptance criterion for #1718 item 1: a manifest
        // with no `kind:` parses cleanly. Discrimination happens at resolve
        // time off the content list.
        var yaml = """
            apiVersion: spring.voyage/v1
            metadata:
              name: noKind
            content:
              - unit: u
            """;

        var manifest = PackageManifestParser.ParseRaw(yaml);
        manifest.Metadata!.Name.ShouldBe("noKind");
        manifest.Content.ShouldNotBeNull();
    }

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public void ParseRaw_LegacyKindField_RejectsWithMigrationMessage()
    {
        // #1718 item 1: a manifest still carrying `kind:` is rejected with
        // an actionable message rather than silently ignored. v0.1 is a
        // clean break — there are no external consumers to migrate.
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: legacy
            content:
              - unit: u
            """;

        var ex = Should.Throw<PackageParseException>(
            () => PackageManifestParser.ParseRaw(yaml));

        ex.Message.ShouldContain("kind");
        ex.Message.ShouldContain("#1718");
    }

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public void ParseRaw_LegacyTopLevelUnitField_RejectsWithMigrationMessage()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            metadata:
              name: legacy
            unit: u
            """;

        var ex = Should.Throw<PackageParseException>(
            () => PackageManifestParser.ParseRaw(yaml));

        ex.Message.ShouldContain("'unit:'");
        ex.Message.ShouldContain("content");
    }

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public void ParseRaw_LegacyTopLevelSubUnitsField_RejectsWithMigrationMessage()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            metadata:
              name: legacy
            content:
              - unit: root
            subUnits:
              - sub
            """;

        var ex = Should.Throw<PackageParseException>(
            () => PackageManifestParser.ParseRaw(yaml));

        ex.Message.ShouldContain("'subUnits:'");
        ex.Message.ShouldContain("members");
    }

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public void ParseRaw_LegacyTopLevelSkillsField_RejectsWithMigrationMessage()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            metadata:
              name: legacy
            content:
              - unit: root
            skills:
              - my-skill
            """;

        var ex = Should.Throw<PackageParseException>(
            () => PackageManifestParser.ParseRaw(yaml));

        ex.Message.ShouldContain("'skills:'");
        ex.Message.ShouldContain("- skill: <name>");
    }

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public void ParseRaw_LegacyTopLevelWorkflowsField_RejectsWithMigrationMessage()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            metadata:
              name: legacy
            content:
              - unit: root
            workflows:
              - my-workflow
            """;

        var ex = Should.Throw<PackageParseException>(
            () => PackageManifestParser.ParseRaw(yaml));

        ex.Message.ShouldContain("'workflows:'");
    }

    // ---- Required-field failures ----------------------------------------

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public void ParseRaw_MissingMetadataName_Throws()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            metadata:
              description: no name here
            content:
              - unit: root
            """;

        var act = () => PackageManifestParser.ParseRaw(yaml);

        Should.Throw<PackageParseException>(act)
            .Message.ShouldContain("metadata.name");
    }

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public void ParseRaw_MissingMetadata_Throws()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            content:
              - unit: root
            """;

        var act = () => PackageManifestParser.ParseRaw(yaml);

        Should.Throw<PackageParseException>(act)
            .Message.ShouldContain("metadata.name");
    }

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public void ParseRaw_EmptyYaml_Throws()
    {
        var act = () => PackageManifestParser.ParseRaw("");

        Should.Throw<PackageParseException>(act);
    }

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public void ParseRaw_InvalidYaml_Throws()
    {
        var yaml = "metadata: [\nbroken: yaml: here";

        var act = () => PackageManifestParser.ParseRaw(yaml);

        Should.Throw<PackageParseException>(act)
            .Message.ShouldContain("YAML");
    }

    // ---- Connector block (#1670) ---------------------------------------

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public void ParseRaw_ConnectorsDefaultInheritAll_ParsesAndDefaults()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            metadata:
              name: my-pkg
            connectors:
              - type: github
                required: true
            content:
              - unit: root-unit
            """;

        var manifest = PackageManifestParser.ParseRaw(yaml);

        //  // manifest.Connectors.ShouldNotBeNull();
        //  // manifest.Connectors!.Count.ShouldBe(1);
        // var entry = manifest.Connectors[0];
        // entry.Type.ShouldBe("github");
        // entry.Required.ShouldBeTrue();
        // entry.InheritAll.ShouldBeTrue();
        // entry.InheritUnits.ShouldBeNull();
    }

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public void ParseRaw_ConnectorsInheritList_ParsesUnitNames()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            metadata:
              name: my-pkg
            connectors:
              - type: github
                inherit:
                  - sv-oss-software-engineering
                  - sv-oss-design
            content:
              - unit: root-unit
            """;

        var manifest = PackageManifestParser.ParseRaw(yaml);

        // var entry = manifest.Connectors!.Single();
        // entry.InheritAll.ShouldBeFalse();
        // entry.InheritUnits.ShouldNotBeNull();
        // entry.InheritUnits!.Count.ShouldBe(2);
        // entry.InheritUnits.ShouldContain("sv-oss-software-engineering");
    }

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public void ParseRaw_ConnectorsInheritAllScalar_DefaultsToInheritAll()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            metadata:
              name: my-pkg
            connectors:
              - type: github
                inherit: all
            content:
              - unit: root-unit
            """;

        var manifest = PackageManifestParser.ParseRaw(yaml);

        // var entry = manifest.Connectors!.Single();
        // entry.InheritAll.ShouldBeTrue();
        // entry.InheritUnits.ShouldBeNull();
    }

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public void ParseRaw_ConnectorsBadInheritScalar_Throws()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            metadata:
              name: my-pkg
            connectors:
              - type: github
                inherit: nonsense
            content:
              - unit: root-unit
            """;

        var act = () => PackageManifestParser.ParseRaw(yaml);

        Should.Throw<PackageParseException>(act)
            .Message.ShouldContain("inherit");
    }

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public void ParseRaw_ConnectorsMissingType_Throws()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            metadata:
              name: my-pkg
            connectors:
              - required: true
            content:
              - unit: root-unit
            """;

        var act = () => PackageManifestParser.ParseRaw(yaml);

        Should.Throw<PackageParseException>(act)
            .Message.ShouldContain("type");
    }

    // ---- Backward compatibility: old single-unit YAML still parses via ManifestParser ----------

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
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