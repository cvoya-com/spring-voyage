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

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
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

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
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

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
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

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
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

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
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

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
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

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
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

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public async Task PackageManifestParser_PathStyleUnitContentEntry_Throws()
    {
        const string Yaml = """
            apiVersion: spring.voyage/v1
            metadata:
              name: my-pkg
            content:
              - unit: unit://eng/backend
            """;

        var ex = await Should.ThrowAsync<PackageParseException>(
            () => PackageManifestParser.ParseAndResolveAsync(Yaml, "/tmp/fake"));

        ex.Message.ShouldContain("unit");
        ex.Message.ShouldContain("local symbol");
    }

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public async Task PackageManifestParser_PathStyleNestedUnitContentEntry_Throws()
    {
        // Post-#1718 item 2: there is no separate `subUnits:` block — every
        // top-level unit reference rides through `content:`. Still must
        // reject path-style values.
        const string Yaml = """
            apiVersion: spring.voyage/v1
            metadata:
              name: my-pkg
            content:
              - unit: root
              - unit: unit://eng/backend
            """;

        var ex = await Should.ThrowAsync<PackageParseException>(
            () => PackageManifestParser.ParseAndResolveAsync(Yaml, "/tmp/fake"));

        ex.Message.ShouldContain("content[1].unit");
    }

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public async Task PackageManifestParser_PathStyleSkillContentEntry_Throws()
    {
        const string Yaml = """
            apiVersion: spring.voyage/v1
            metadata:
              name: my-pkg
            content:
              - unit: root
              - skill: skill://my-skill
            """;

        var ex = await Should.ThrowAsync<PackageParseException>(
            () => PackageManifestParser.ParseAndResolveAsync(Yaml, "/tmp/fake"));

        ex.Message.ShouldContain("content[1].skill");
    }

    [Fact(Skip = "Updated in #1727 — ADR-0037 impl 4/4")]
    public async Task PackageManifestParser_PathStyleWorkflowContentEntry_Throws()
    {
        const string Yaml = """
            apiVersion: spring.voyage/v1
            metadata:
              name: my-pkg
            content:
              - unit: root
              - workflow: workflow://ci
            """;

        var ex = await Should.ThrowAsync<PackageParseException>(
            () => PackageManifestParser.ParseAndResolveAsync(Yaml, "/tmp/fake"));

        ex.Message.ShouldContain("content[1].workflow");
    }
}