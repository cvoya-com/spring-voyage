// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using System.IO;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

/// <summary>
/// ADR-0043 §3 unit-member-scope rule: a unit's bare <c>- agent:</c> /
/// <c>- unit:</c> reference must resolve to an artefact owned by the
/// declaring unit. Owned means (a) nested under the unit's own
/// <c>agents/</c> / <c>units/</c> folder, or (b) synthesised from an
/// inline body. Bare names that resolve up to a top-level peer or
/// sideways into a sibling unit are rejected with
/// <see cref="Adr0043ParseErrors.UnitMemberOutOfScope"/>.
/// </summary>
public class UnitMemberScopeTests
{
    [Fact]
    public async Task NestedAgentMember_Accepted()
    {
        using var pkg = BuildPackage(new[]
        {
            ("units/team/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: Unit
                name: team
                description: x
                members:
                  - agent: worker
                """),
            ("units/team/agents/worker/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: Agent
                name: worker
                description: x
                """),
        });

        var resolved = await ParseAsync(pkg.Root);

        resolved.Units.ShouldHaveSingleItem();
        resolved.Agents.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task TopLevelAgentReferencedFromUnitMembers_Rejected()
    {
        using var pkg = BuildPackage(new[]
        {
            ("units/team/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: Unit
                name: team
                description: x
                members:
                  - agent: worker
                """),
            ("agents/worker/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: Agent
                name: worker
                description: x
                """),
        });

        var ex = await Should.ThrowAsync<PackageParseException>(() => ParseAsync(pkg.Root));
        ex.Message.ShouldContain("UnitMemberOutOfScope");
        ex.Message.ShouldContain("team");
        ex.Message.ShouldContain("worker");
        ex.Message.ShouldContain("top-level artefact");
    }

    [Fact]
    public async Task SiblingUnitAgentReferenced_Rejected()
    {
        using var pkg = BuildPackage(new[]
        {
            ("units/team-a/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: Unit
                name: team-a
                description: x
                members:
                  - agent: foreigner
                """),
            ("units/team-b/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: Unit
                name: team-b
                description: x
                """),
            ("units/team-b/agents/foreigner/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: Agent
                name: foreigner
                description: x
                """),
        });

        var ex = await Should.ThrowAsync<PackageParseException>(() => ParseAsync(pkg.Root));
        ex.Message.ShouldContain("UnitMemberOutOfScope");
        ex.Message.ShouldContain("owned by unit 'team-b'");
    }

    [Fact]
    public async Task InlineAgentMember_Accepted()
    {
        using var pkg = BuildPackage(new[]
        {
            ("units/team/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: Unit
                name: team
                description: x
                members:
                  - agent:
                      name: ada
                      description: x
                """),
        });

        var resolved = await ParseAsync(pkg.Root);

        resolved.Units.ShouldHaveSingleItem();
        var ada = resolved.Agents.ShouldHaveSingleItem();
        ada.Name.ShouldBe("ada");
    }

    [Fact]
    public async Task CrossPackageQualifiedReference_RidesThrough()
    {
        // ADR-0037 §5 cross-package address: `<pkg>/<name>` is parsed as
        // cross-package by ArtefactReference.Parse and the scope validator
        // ignores it (the catalog provider resolves it later).
        using var pkg = BuildPackage(new[]
        {
            ("units/team/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: Unit
                name: team
                description: x
                members:
                  - agent: shared-pack/utility
                """),
        });

        var resolved = await ParseAsync(pkg.Root);
        resolved.Units.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task UnresolvedBareName_RidesThrough()
    {
        // The bare name doesn't resolve to any in-package artefact — the
        // historical activator auto-register path takes over at install
        // time, so the validator does not surface a parse error.
        using var pkg = BuildPackage(new[]
        {
            ("units/team/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: Unit
                name: team
                description: x
                members:
                  - agent: not-here
                """),
        });

        var resolved = await ParseAsync(pkg.Root);
        resolved.Units.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task NestedSubUnitMember_Accepted()
    {
        using var pkg = BuildPackage(new[]
        {
            ("units/parent/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: Unit
                name: parent
                description: x
                members:
                  - unit: child
                """),
            ("units/parent/units/child/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: Unit
                name: child
                description: x
                """),
        });

        var resolved = await ParseAsync(pkg.Root);
        resolved.Units.Count.ShouldBe(2);
    }

    [Fact]
    public async Task TopLevelSubUnitReference_Rejected()
    {
        using var pkg = BuildPackage(new[]
        {
            ("units/parent/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: Unit
                name: parent
                description: x
                members:
                  - unit: floater
                """),
            ("units/floater/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: Unit
                name: floater
                description: x
                """),
        });

        var ex = await Should.ThrowAsync<PackageParseException>(() => ParseAsync(pkg.Root));
        ex.Message.ShouldContain("UnitMemberOutOfScope");
        ex.Message.ShouldContain("unit: floater");
    }

    private const string PackageHeaderYaml = """
        apiVersion: spring.voyage/v1
        kind: Package
        name: scope-test
        description: x
        version: 1.0.0
        """;

    private static async Task<ResolvedPackage> ParseAsync(string root)
    {
        var yaml = await File.ReadAllTextAsync(Path.Combine(root, "package.yaml"));
        return await PackageManifestParser.ParseAndResolveAsync(yaml, root);
    }

    private static TempPackage BuildPackage((string Path, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "sv-scope-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.yaml"), PackageHeaderYaml);
        foreach (var (rel, content) in files)
        {
            var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        return new TempPackage(root);
    }

    private sealed class TempPackage : IDisposable
    {
        public string Root { get; }
        public TempPackage(string root) { Root = root; }
        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch { /* best effort */ }
        }
    }
}
