// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using System.Threading.Tasks;

using Shouldly;

using Xunit;

/// <summary>
/// End-to-end tests that exercise the parser's rejection of obsolete
/// grammar shapes (#1629 PR7). The parser must fail fast with an actionable
/// error pointing at the offending field; silent fall-back is explicitly
/// out of scope for v0.1.
/// </summary>
public class ManifestGrammarRejectionTests
{
    // ── Unit-manifest layer ────────────────────────────────────────────────

    [Fact]
    public void ManifestParser_PathStyleAgentRef_Throws()
    {
        const string Yaml = """
            unit:
              name: my-unit
              members:
                - agent: agent://eng/alice
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(Yaml));

        ex.Message.ShouldContain("agent://eng/alice");
        ex.Message.ShouldContain("local symbol");
    }

    [Fact]
    public void ManifestParser_PathStyleUnitRef_Throws()
    {
        const string Yaml = """
            unit:
              name: my-unit
              members:
                - unit: unit://eng/backend
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(Yaml));

        ex.Message.ShouldContain("unit://eng/backend");
    }

    [Fact]
    public void ManifestParser_BothAgentAndUnit_Throws()
    {
        const string Yaml = """
            unit:
              name: my-unit
              members:
                - agent: alice
                  unit: backend
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(Yaml));

        ex.Message.ShouldContain("members[0]");
        ex.Message.ShouldContain("both");
    }

    [Fact]
    public void ManifestParser_NeitherAgentNorUnit_Throws()
    {
        const string Yaml = """
            unit:
              name: my-unit
              members:
                - description: empty member
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(Yaml));

        ex.Message.ShouldContain("members[0]");
        ex.Message.ShouldContain("missing");
    }

    [Fact]
    public void ManifestParser_DuplicateMemberSymbol_Throws()
    {
        const string Yaml = """
            unit:
              name: my-unit
              members:
                - agent: alice
                - agent: alice
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(Yaml));

        ex.Message.ShouldContain("alice");
        ex.Message.ShouldContain("more than once");
    }

    [Fact]
    public void ManifestParser_LocalSymbolMembers_Succeed()
    {
        // The new IaC-style local-symbol grammar parses cleanly.
        const string Yaml = """
            unit:
              name: u_eng
              description: Engineering team
              members:
                - agent: a_alice
                - agent: a_bob
                - unit: u_subteam
            """;

        var manifest = ManifestParser.Parse(Yaml);

        manifest.Members.ShouldNotBeNull();
        manifest.Members!.Count.ShouldBe(3);
        manifest.Members[0].Agent.ShouldBe("a_alice");
        manifest.Members[1].Agent.ShouldBe("a_bob");
        manifest.Members[2].Unit.ShouldBe("u_subteam");
    }

    [Fact]
    public void ManifestParser_GuidMemberRefs_Succeed()
    {
        // 32-char no-dash hex Guids in member refs are treated as
        // cross-package references and parse without error.
        const string Yaml = """
            unit:
              name: u_eng
              members:
                - agent: 8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7
            """;

        var manifest = ManifestParser.Parse(Yaml);

        manifest.Members!.Count.ShouldBe(1);
        manifest.Members[0].Agent.ShouldBe("8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7");
    }

    // ── Package-manifest layer ─────────────────────────────────────────────

    [Fact]
    public async Task PackageManifestParser_PathStyleUnitSlot_Throws()
    {
        const string Yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: my-pkg
            unit: unit://eng/backend
            """;

        var ex = await Should.ThrowAsync<PackageParseException>(
            () => PackageManifestParser.ParseAndResolveAsync(Yaml, "/tmp/fake"));

        ex.Message.ShouldContain("unit");
        ex.Message.ShouldContain("local symbol");
    }

    [Fact]
    public async Task PackageManifestParser_PathStyleSubUnitEntry_Throws()
    {
        const string Yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: my-pkg
            unit: root
            subUnits:
              - unit://eng/backend
            """;

        var ex = await Should.ThrowAsync<PackageParseException>(
            () => PackageManifestParser.ParseAndResolveAsync(Yaml, "/tmp/fake"));

        ex.Message.ShouldContain("subUnits[0]");
    }

    [Fact]
    public async Task PackageManifestParser_PathStyleSkillEntry_Throws()
    {
        const string Yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: my-pkg
            unit: root
            skills:
              - skill://my-skill
            """;

        var ex = await Should.ThrowAsync<PackageParseException>(
            () => PackageManifestParser.ParseAndResolveAsync(Yaml, "/tmp/fake"));

        ex.Message.ShouldContain("skills[0]");
    }

    [Fact]
    public async Task PackageManifestParser_PathStyleWorkflowEntry_Throws()
    {
        const string Yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: my-pkg
            unit: root
            workflows:
              - workflow://ci
            """;

        var ex = await Should.ThrowAsync<PackageParseException>(
            () => PackageManifestParser.ParseAndResolveAsync(Yaml, "/tmp/fake"));

        ex.Message.ShouldContain("workflows[0]");
    }
}