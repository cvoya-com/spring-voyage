// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using Shouldly;

using Xunit;

/// <summary>
/// Parser coverage for the top-level <c>displayName:</c> manifest slot.
/// Verifies the field is parsed off every artefact kind that declares it
/// (<c>Unit</c>, <c>Agent</c>, <c>UnitTemplate</c>, <c>AgentTemplate</c>)
/// and that omitting it leaves the property <c>null</c> so the install
/// pipeline can fall back to the canonical <c>name:</c> field.
/// </summary>
public class ManifestDisplayNameTests
{
    // ---- Unit -----------------------------------------------------------

    [Fact]
    public void Parse_UnitManifest_WithDisplayName_PopulatesProperty()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: sv-oss-software-engineering
            displayName: Software Engineering
            description: SE team for the OSS dogfooding org.
            """;

        var manifest = ManifestParser.Parse(yaml);

        manifest.Name.ShouldBe("sv-oss-software-engineering");
        manifest.DisplayName.ShouldBe("Software Engineering");
    }

    [Fact]
    public void Parse_UnitManifest_WithoutDisplayName_LeavesPropertyNull()
    {
        // The historical shape — no `displayName:` slot — must continue
        // to parse cleanly so existing packages don't break.
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: legacy-unit
            description: A unit declared before the displayName slot existed.
            """;

        var manifest = ManifestParser.Parse(yaml);

        manifest.DisplayName.ShouldBeNull();
    }

    // ---- Agent -----------------------------------------------------------
    //
    // Agent manifests aren't run through a dedicated `ManifestParser`
    // overload — the installer parses them inline through YamlDotNet — so
    // round-trip via a deserializer matching the activator's reader.

    [Fact]
    public void Parse_AgentManifest_WithDisplayName_PopulatesProperty()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Agent
            name: ada
            displayName: Ada (engineer)
            description: A friendly OSS engineer.
            """;

        var manifest = DeserializeAgent(yaml);

        manifest.Name.ShouldBe("ada");
        manifest.DisplayName.ShouldBe("Ada (engineer)");
    }

    [Fact]
    public void Parse_AgentManifest_WithoutDisplayName_LeavesPropertyNull()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Agent
            name: legacy-agent
            description: An agent declared before the displayName slot existed.
            """;

        var manifest = DeserializeAgent(yaml);

        manifest.DisplayName.ShouldBeNull();
    }

    // ---- UnitTemplate ----------------------------------------------------

    [Fact]
    public void Parse_UnitTemplate_WithDisplayName_PopulatesProperty()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitTemplate
            name: software-team
            displayName: Software Team
            description: A reusable software-team template.
            """;

        var manifest = ManifestParser.ParseUnitTemplate(yaml);

        manifest.Name.ShouldBe("software-team");
        manifest.DisplayName.ShouldBe("Software Team");
    }

    [Fact]
    public void Parse_UnitTemplate_WithoutDisplayName_LeavesPropertyNull()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitTemplate
            name: legacy-template
            description: A template declared before displayName existed.
            """;

        var manifest = ManifestParser.ParseUnitTemplate(yaml);

        manifest.DisplayName.ShouldBeNull();
    }

    // ---- AgentTemplate ---------------------------------------------------

    [Fact]
    public void Parse_AgentTemplate_WithDisplayName_PopulatesProperty()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: AgentTemplate
            name: software-engineer
            displayName: Software Engineer
            description: A reusable engineer template.
            """;

        var manifest = ManifestParser.ParseAgentTemplate(yaml);

        manifest.Name.ShouldBe("software-engineer");
        manifest.DisplayName.ShouldBe("Software Engineer");
    }

    [Fact]
    public void Parse_AgentTemplate_WithoutDisplayName_LeavesPropertyNull()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: AgentTemplate
            name: legacy-template
            description: A template declared before displayName existed.
            """;

        var manifest = ManifestParser.ParseAgentTemplate(yaml);

        manifest.DisplayName.ShouldBeNull();
    }

    // ADR-0043 §5d note: the resolver's MergeYaml dispatches on YAML
    // shape — scalars → instance wins, template fills the gap. Since
    // `displayName:` is a scalar at the top level, the existing merge
    // logic already handles the instance-wins / template-fills rule
    // without any displayName-specific code. The TemplateResolverTests
    // suite covers the merge invariants; the parser tests above lock
    // down that the field actually flows through deserialisation on
    // both the template and instance sides, which is the new surface.

    private static AgentManifest DeserializeAgent(string yaml)
    {
        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<AgentManifest>(yaml)!;
    }
}
