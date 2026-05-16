// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Artefacts;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for ADR-0043 §5g — inline-mapping members on a unit's
/// <c>members:</c> list. The unit YAML may carry either a bare scalar
/// reference (<c>- agent: ada</c>) or an inline body
/// (<c>- agent: { name: ada, from: software-engineer, displayName: ... }</c>).
/// The inline form is canonical for "fan N instances out of one template"
/// — each entry stamps a fresh concrete child of the unit using the
/// inline body's overrides.
/// </summary>
public class InlineMemberParsingTests
{
    [Fact]
    public void ParseUnit_InlineAgentMemberWithFrom_DeserialisesAsInlineBody()
    {
        const string yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: engineering
            description: x
            members:
              - agent: { name: ada, from: software-engineer, displayName: "Ada (engineer)" }
              - agent: hopper
            """;

        var manifest = ManifestParser.Parse(yaml);

        manifest.Members.ShouldNotBeNull();
        manifest.Members!.Count.ShouldBe(2);

        // Inline mapping member captured under InlineArtefactDefinition.
        var inline = manifest.Members[0].Agent;
        inline.ShouldNotBeNull();
        inline!.IsInline.ShouldBeTrue();
        inline.InlineName.ShouldBe("ada");
        // The captured body carries every authored field — name + from +
        // displayName — so the install pipeline can stamp a fresh agent
        // by cloning the template and overlaying the overrides.
        inline.InlineBody.ShouldNotBeNull();
        inline.InlineBody!.ShouldContain("name: ada");
        inline.InlineBody!.ShouldContain("from: software-engineer");
        inline.InlineBody!.ShouldContain("Ada (engineer)");

        // The AgentName helper collapses both forms to a single string the
        // downstream uniqueness / cycle / member-resolution logic keys off.
        manifest.Members[0].AgentName.ShouldBe("ada");

        // Bare-scalar member round-trips through the same shape — the
        // converter returns a Reference rather than an inline body.
        manifest.Members[1].Agent.ShouldNotBeNull();
        manifest.Members[1].Agent!.IsInline.ShouldBeFalse();
        manifest.Members[1].Agent!.Reference.ShouldBe("hopper");
        manifest.Members[1].AgentName.ShouldBe("hopper");
    }

    [Fact]
    public void ParseUnit_InlineUnitMemberWithFrom_DeserialisesAsInlineBody()
    {
        const string yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: org
            description: x
            members:
              - unit: { name: foo, from: some-unit-template }
            """;

        var manifest = ManifestParser.Parse(yaml);

        var inline = manifest.Members![0].Unit;
        inline.ShouldNotBeNull();
        inline!.IsInline.ShouldBeTrue();
        inline.InlineName.ShouldBe("foo");
        inline.InlineBody!.ShouldContain("from: some-unit-template");
        manifest.Members[0].UnitName.ShouldBe("foo");
    }

    [Fact]
    public void ParseUnit_InlineMember_MissingName_RejectedWithActionableMessage()
    {
        // ADR-0043 §5g: an inline body's `name:` is the local symbol the
        // owning unit references and the install pipeline keys identity off.
        // An anonymous inline body has no symbol — reject at parse time.
        const string yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: engineering
            description: x
            members:
              - agent: { from: software-engineer, displayName: "Anonymous" }
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));
        ex.Message.ShouldContain("inline body is missing the required 'name:' field");
    }

    [Fact]
    public async Task ParseAndResolve_InlineFromMembers_SynthesisesFreshAgentArtefacts()
    {
        // ADR-0043 §5g end-to-end: a unit with three inline `from:` members
        // produces three synthesised concrete agents in the resolved package.
        // Each synthesised agent is a peer in `package.Agents` with its
        // ContainingArtefactName recording the owning unit, so the install
        // pipeline activates it identically to a disk-discovered agent.
        var packageRoot = await BuildPackageAsync(
            ("templates/software-engineer/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: AgentTemplate
                name: software-engineer
                description: shared engineer body.
                role: software-engineer
                ai:
                  runtime: claude-code
                  model:
                    provider: anthropic
                    id: claude-opus-4-7
                """),
            ("units/engineering/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: Unit
                name: engineering
                description: x
                execution:
                  image: ghcr.io/example/eng:latest
                members:
                  - agent: { name: ada,     from: software-engineer, displayName: "Ada (engineer)" }
                  - agent: { name: hopper,  from: software-engineer, displayName: "Hopper (engineer)" }
                  - agent: { name: turing,  from: software-engineer, displayName: "Turing (engineer)" }
                """));

        try
        {
            var yaml = await File.ReadAllTextAsync(
                Path.Combine(packageRoot, "package.yaml"),
                TestContext.Current.CancellationToken);
            var resolved = await PackageManifestParser.ParseAndResolveAsync(
                yaml, packageRoot,
                cancellationToken: TestContext.Current.CancellationToken);

            // Three synthesised agents land in the resolved package — names
            // taken from each inline body's `name:` field. (The package's
            // `software-engineer` AgentTemplate also surfaces under
            // `package.Agents` at this stage; templates are stripped by
            // TemplateResolver downstream — see TemplateResolverTests for
            // the post-stamping shape.)
            var synthesisedAgents = resolved.Agents
                .Where(a => a.ContainingArtefactName == "engineering")
                .ToList();
            synthesisedAgents.Select(a => a.Name).OrderBy(n => n).ShouldBe(new[] { "ada", "hopper", "turing" });

            foreach (var agent in synthesisedAgents)
            {
                // Containment edge: each synthesised agent is nested under
                // its owning unit. The classification used by the install
                // pipeline's top-level scan keeps them out of the package's
                // top-level activatables list.
                agent.ContainingArtefactName.ShouldBe("engineering");
                agent.IsTopLevel.ShouldBeFalse();

                // The synthesised content is a self-contained Agent YAML
                // header so the per-kind parser walks it identically to a
                // disk-discovered artefact.
                agent.Content.ShouldNotBeNull();
                agent.Content!.ShouldContain("kind: Agent");
                agent.Content!.ShouldContain($"name: {agent.Name}");
                // The inline body's `from:` and `displayName:` survive
                // through synthesis so TemplateResolver picks them up.
                agent.Content!.ShouldContain("from: software-engineer");
            }

            // The owning unit still surfaces with its three members — the
            // synthesis step does not alter the unit's persisted content.
            var unit = resolved.Units.Single(u => u.Name == "engineering");
            unit.Content!.ShouldContain("Ada (engineer)");
        }
        finally
        {
            if (Directory.Exists(packageRoot))
            {
                Directory.Delete(packageRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ParseAndResolve_InlineMember_NameCollidesWithDiskArtefact_Rejected()
    {
        // ADR-0043 §3 + §5g: synthesised inline members must not shadow a
        // disk-discovered artefact. Two artefacts of the same kind sharing
        // a name would break member resolution at install time.
        var packageRoot = await BuildPackageAsync(
            ("agents/ada/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: Agent
                name: ada
                description: x
                """),
            ("units/engineering/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: Unit
                name: engineering
                description: x
                members:
                  - agent: { name: ada, from: software-engineer }
                """));

        try
        {
            var yaml = await File.ReadAllTextAsync(
                Path.Combine(packageRoot, "package.yaml"),
                TestContext.Current.CancellationToken);

            var ex = await Should.ThrowAsync<PackageParseException>(
                async () => await PackageManifestParser.ParseAndResolveAsync(
                    yaml, packageRoot,
                    cancellationToken: TestContext.Current.CancellationToken));
            ex.Message.ShouldContain("Duplicate artefact name");
            ex.Message.ShouldContain("Agent:ada");
        }
        finally
        {
            if (Directory.Exists(packageRoot))
            {
                Directory.Delete(packageRoot, recursive: true);
            }
        }
    }

    private const string PackageHeaderYaml = """
        apiVersion: spring.voyage/v1
        kind: Package
        name: inline-members-test
        description: An inline-member-test package.
        version: 1.0.0
        """;

    private static async Task<string> BuildPackageAsync(params (string Path, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "sv-inline-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "package.yaml"), PackageHeaderYaml);
        foreach (var (rel, content) in files)
        {
            var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
        }
        return root;
    }
}
