// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using System.IO;

using Shouldly;

using Xunit;

/// <summary>
/// Parser tests for ADR-0043 chunk 1 (recursive package format —
/// additive manifest-layer changes). Covers:
/// <list type="bullet">
///   <item><description>§5 — new <c>AgentTemplate</c> / <c>UnitTemplate</c> kinds.</description></item>
///   <item><description>§5 — <c>from:</c> field parsed on Agent / Unit / AgentTemplate / UnitTemplate.</description></item>
///   <item><description>#2298 — top-level <c>instructions:</c> hoisted onto <c>UnitManifest</c>.</description></item>
///   <item><description>§8 — migration error-code constants are defined with the expected message text.</description></item>
/// </list>
/// </summary>
public class Adr0043Tests
{
    private static readonly string FixtureRoot = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "Packages-Recursive");

    // ---- §5 — AgentTemplate parses through the new entry point ----------

    [Fact]
    public void ParseAgentTemplate_FromFixture_Succeeds()
    {
        var yaml = File.ReadAllText(Path.Combine(FixtureRoot, "templates", "agent-template.yaml"));

        var manifest = ManifestParser.ParseAgentTemplate(yaml);

        manifest.ApiVersion.ShouldBe("spring.voyage/v1");
        manifest.Kind.ShouldBe("AgentTemplate");
        manifest.Name.ShouldBe("software-engineer");
        manifest.Description.ShouldBe("A reusable software-engineer archetype.");
        manifest.Role.ShouldBe("engineer");
        manifest.Capabilities.ShouldNotBeNull();
        manifest.Capabilities!.Count.ShouldBe(2);
        manifest.Ai.ShouldNotBeNull();
        manifest.Ai!.Runtime.ShouldBe("claude-code");
        manifest.Ai.Model.ShouldNotBeNull();
        manifest.Ai.Model!.Provider.ShouldBe("anthropic");
        manifest.Ai.Model.Id.ShouldBe("claude-opus-4-7");
        manifest.Instructions.ShouldNotBeNull();
        manifest.Instructions!.ShouldContain("senior software engineer");
        manifest.Expertise.ShouldNotBeNull();
        manifest.Expertise!.Count.ShouldBe(1);
        manifest.Expertise[0].Domain.ShouldBe("dotnet");
        manifest.Requires.ShouldNotBeNull();
        manifest.Requires!.Count.ShouldBe(1);
        manifest.Requires[0].Type.ShouldBe(RequirementType.Connector);
        manifest.Requires[0].Identifier.ShouldBe("github");
    }

    [Fact]
    public void ParseAgentTemplate_MismatchedKind_Rejected()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Agent
            name: not-a-template
            description: x
            """;

        var ex = Should.Throw<ManifestParseException>(
            () => ManifestParser.ParseAgentTemplate(yaml));
        ex.Message.ShouldContain("AgentTemplate");
    }

    // ---- §5 — UnitTemplate parses through the new entry point -----------

    [Fact]
    public void ParseUnitTemplate_FromFixture_Succeeds()
    {
        var yaml = File.ReadAllText(Path.Combine(FixtureRoot, "templates", "unit-template.yaml"));

        var manifest = ManifestParser.ParseUnitTemplate(yaml);

        manifest.ApiVersion.ShouldBe("spring.voyage/v1");
        manifest.Kind.ShouldBe("UnitTemplate");
        manifest.Name.ShouldBe("engineering-team");
        manifest.Description.ShouldBe("A reusable engineering-team archetype.");
        manifest.Ai.ShouldNotBeNull();
        manifest.Ai!.Runtime.ShouldBe("spring-voyage");
        manifest.Instructions.ShouldNotBeNull();
        manifest.Instructions!.ShouldContain("Coordinate the team");
        manifest.Members.ShouldNotBeNull();
        manifest.Members!.Count.ShouldBe(2);
        manifest.Members[0].AgentName.ShouldBe("team-lead");
        manifest.Members[1].AgentName.ShouldBe("senior-engineer");
        manifest.Requires.ShouldNotBeNull();
        manifest.Requires!.Count.ShouldBe(1);
        manifest.Requires[0].Type.ShouldBe(RequirementType.Connector);
        manifest.Requires[0].Identifier.ShouldBe("github");
    }

    [Fact]
    public void ParseUnitTemplate_MismatchedKind_Rejected()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: not-a-template
            description: x
            """;

        var ex = Should.Throw<ManifestParseException>(
            () => ManifestParser.ParseUnitTemplate(yaml));
        ex.Message.ShouldContain("UnitTemplate");
    }

    // ---- §5 — `from:` parses on every relevant kind ---------------------

    [Fact]
    public void AgentManifest_FromBareName_Parsed()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Agent
            name: ada
            description: An instance of the software-engineer archetype.
            from: software-engineer
            """;

        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new RequirementEntryYamlConverter())
            .IgnoreUnmatchedProperties()
            .Build();
        var manifest = deserializer.Deserialize<AgentManifest>(yaml);

        manifest.From.ShouldBe("software-engineer");
    }

    [Fact]
    public void AgentManifest_FromQualifiedReference_Parsed()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Agent
            name: ada
            description: Instance of cross-package template.
            from: shared/software-engineer@1.0.0
            """;

        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new RequirementEntryYamlConverter())
            .IgnoreUnmatchedProperties()
            .Build();
        var manifest = deserializer.Deserialize<AgentManifest>(yaml);

        manifest.From.ShouldBe("shared/software-engineer@1.0.0");
    }

    [Fact]
    public void UnitManifest_FromBareName_Parsed()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: engineering-1
            description: An instance of the engineering-team archetype.
            from: engineering-team
            """;

        var manifest = ManifestParser.Parse(yaml);

        manifest.From.ShouldBe("engineering-team");
    }

    [Fact]
    public void UnitManifest_FromQualifiedReference_Parsed()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: engineering-1
            description: An instance of a cross-package template.
            from: shared/engineering-team@1.0.0
            """;

        var manifest = ManifestParser.Parse(yaml);

        manifest.From.ShouldBe("shared/engineering-team@1.0.0");
    }

    [Fact]
    public void AgentTemplateManifest_FromBareName_Parsed()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: AgentTemplate
            name: senior-engineer
            description: A template that chains another template.
            from: software-engineer
            """;

        var manifest = ManifestParser.ParseAgentTemplate(yaml);

        manifest.From.ShouldBe("software-engineer");
    }

    [Fact]
    public void UnitTemplateManifest_FromQualifiedReference_Parsed()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitTemplate
            name: special-engineering-team
            description: A template that chains another cross-package template.
            from: shared/engineering-team@1.0.0
            """;

        var manifest = ManifestParser.ParseUnitTemplate(yaml);

        manifest.From.ShouldBe("shared/engineering-team@1.0.0");
    }

    // ---- #2298 — top-level instructions on UnitManifest -----------------

    [Fact]
    public void UnitManifest_TopLevelInstructions_Parsed()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: my-unit
            description: A unit with hoisted instructions.
            instructions: |
              You are the coordinator of this unit.
              Keep replies short.
            """;

        var manifest = ManifestParser.Parse(yaml);

        manifest.Name.ShouldBe("my-unit");
        manifest.Instructions.ShouldNotBeNull();
        manifest.Instructions!.ShouldContain("coordinator of this unit");
        manifest.Instructions.ShouldContain("Keep replies short");
    }

    // ---- §8 — migration error-code constants ----------------------------

    [Fact]
    public void Adr0043ParseErrors_LegacyFlatArtefactLayout_HasExpectedText()
    {
        Adr0043ParseErrors.LegacyFlatArtefactLayout.ShouldContain("LegacyFlatArtefactLayout");
        Adr0043ParseErrors.LegacyFlatArtefactLayout.ShouldContain("artefact must be a folder rooted at `package.yaml`");
        Adr0043ParseErrors.LegacyFlatArtefactLayout.ShouldContain("./agents/foo.yaml");
        Adr0043ParseErrors.LegacyFlatArtefactLayout.ShouldContain("./agents/foo/package.yaml");
    }

    [Fact]
    public void Adr0043ParseErrors_LegacyContentField_HasExpectedText()
    {
        Adr0043ParseErrors.LegacyContentField.ShouldContain("LegacyContentField");
        Adr0043ParseErrors.LegacyContentField.ShouldContain("content: is removed in ADR-0043");
        Adr0043ParseErrors.LegacyContentField.ShouldContain("directory layout");
    }

    [Fact]
    public void Adr0043ParseErrors_UnexpectedInnerVersion_HasExpectedText()
    {
        Adr0043ParseErrors.UnexpectedInnerVersion.ShouldContain("UnexpectedInnerVersion");
        Adr0043ParseErrors.UnexpectedInnerVersion.ShouldContain("version: lives only on the install-root package.yaml");
        Adr0043ParseErrors.UnexpectedInnerVersion.ShouldContain("inner artefacts inherit from the container");
    }

    [Fact]
    public void Adr0043ParseErrors_ArtefactFolderNameMismatch_HasExpectedText()
    {
        Adr0043ParseErrors.ArtefactFolderNameMismatch.ShouldContain("ArtefactFolderNameMismatch");
        Adr0043ParseErrors.ArtefactFolderNameMismatch.ShouldContain("the folder name must equal the name: field of its");
        Adr0043ParseErrors.ArtefactFolderNameMismatch.ShouldContain("package.yaml");
    }

    [Fact]
    public void Adr0043ParseErrors_LegacyAiPromptField_HasExpectedText()
    {
        Adr0043ParseErrors.LegacyAiPromptField.ShouldContain("LegacyAiPromptField");
        Adr0043ParseErrors.LegacyAiPromptField.ShouldContain("ai.prompt: is removed in ADR-0043");
        Adr0043ParseErrors.LegacyAiPromptField.ShouldContain("top-level instructions:");
    }
}
