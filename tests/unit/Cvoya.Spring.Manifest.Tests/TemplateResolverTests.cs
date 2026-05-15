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
/// Tests for <see cref="TemplateResolver"/> — the ADR-0043 §5 stamping
/// step that resolves <c>from:</c> references on concrete artefacts and
/// clones the template's nested concrete children as fresh children of
/// the consumer.
/// </summary>
public class TemplateResolverTests
{
    [Fact]
    public async Task ResolveAsync_NoFromReferences_PassesPackageThrough()
    {
        using var pkg = BuildPackage(
            files: new[]
            {
                ("units/alpha/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    """),
            });

        var resolved = await ParseAsync(pkg.Root);
        var resolver = new TemplateResolver();
        var output = await resolver.ResolveAsync(resolved, pkg.Root, TestContext.Current.CancellationToken);

        output.Units.Count.ShouldBe(1);
        output.Units[0].Name.ShouldBe("alpha");
        output.Agents.Count.ShouldBe(0);
    }

    [Fact]
    public async Task ResolveAsync_AgentFromAgentTemplate_BodyMergedAndTemplateStripped()
    {
        // The template declares `role: engineer` and `instructions: …`.
        // The consumer overrides `role:` with its own value and leaves
        // `instructions:` unset — the template's instructions should
        // flow through.
        using var pkg = BuildPackage(
            files: new[]
            {
                ("templates/engineer/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: AgentTemplate
                    name: engineer
                    description: An engineering archetype.
                    role: engineer
                    instructions: |
                        You are a software engineer.
                    """),
                ("agents/ada/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Agent
                    name: ada
                    description: x
                    from: engineer
                    role: senior-engineer
                    """),
            });

        var resolved = await ParseAsync(pkg.Root);
        var resolver = new TemplateResolver();
        var output = await resolver.ResolveAsync(resolved, pkg.Root, TestContext.Current.CancellationToken);

        // Template is stripped from the output.
        output.Agents.Where(a => a.Name == "engineer").ShouldBeEmpty();

        var ada = output.Agents.Single(a => a.Name == "ada");
        ada.Content.ShouldNotBeNull();
        var adaContent = ada.Content!;
        adaContent.ShouldContain("role: senior-engineer");  // consumer wins
        adaContent.ShouldContain("You are a software engineer.");  // template fills
        // Reserved keys: the consumer's kind / name win, template's kind
        // (`AgentTemplate`) does not leak through.
        adaContent.ShouldContain("kind: Agent");
        adaContent.ShouldNotContain("kind: AgentTemplate");
    }

    [Fact]
    public async Task ResolveAsync_ListField_ConsumerReplacesTemplate()
    {
        // ADR-0043 §5d: lists replace. The template ships an `expertise:`
        // list with two domains; the consumer ships its own one-element
        // list. The consumer's list wins entirely — template entries do
        // NOT survive.
        using var pkg = BuildPackage(
            files: new[]
            {
                ("templates/engineer/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: AgentTemplate
                    name: engineer
                    description: x
                    expertise:
                      - domain: dotnet
                      - domain: web
                    """),
                ("agents/ada/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Agent
                    name: ada
                    description: x
                    from: engineer
                    expertise:
                      - domain: rust
                    """),
            });

        var resolved = await ParseAsync(pkg.Root);
        var output = await new TemplateResolver().ResolveAsync(
            resolved, pkg.Root, TestContext.Current.CancellationToken);

        var ada = output.Agents.Single(a => a.Name == "ada");
        var adaContent = ada.Content!;
        adaContent.ShouldContain("domain: rust");
        adaContent.ShouldNotContain("domain: dotnet");
        adaContent.ShouldNotContain("domain: web");
    }

    [Fact]
    public async Task ResolveAsync_MapField_DeepMergesWithConsumerKeysWinning()
    {
        // ADR-0043 §5d: maps deep-merge. The template ships
        // `ai: { runtime: claude-code, model: { provider: anthropic, id: claude-opus-4-7 } }`.
        // The consumer ships `ai: { model: { id: claude-sonnet-4-7 } }`.
        // The merged shape should carry runtime + provider from the
        // template and id from the consumer.
        using var pkg = BuildPackage(
            files: new[]
            {
                ("templates/engineer/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: AgentTemplate
                    name: engineer
                    description: x
                    ai:
                      runtime: claude-code
                      model:
                        provider: anthropic
                        id: claude-opus-4-7
                    """),
                ("agents/ada/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Agent
                    name: ada
                    description: x
                    from: engineer
                    ai:
                      model:
                        id: claude-sonnet-4-7
                    """),
            });

        var resolved = await ParseAsync(pkg.Root);
        var output = await new TemplateResolver().ResolveAsync(
            resolved, pkg.Root, TestContext.Current.CancellationToken);

        var ada = output.Agents.Single(a => a.Name == "ada");
        var adaContent = ada.Content!;
        adaContent.ShouldContain("runtime: claude-code");              // template fills the gap
        adaContent.ShouldContain("provider: anthropic");                 // template fills the gap
        adaContent.ShouldContain("id: claude-sonnet-4-7");               // consumer wins
        adaContent.ShouldNotContain("id: claude-opus-4-7");
    }

    [Fact]
    public async Task ResolveAsync_UnitFromUnitTemplate_MembersListReplaces()
    {
        // ADR-0043 §5d: `members:` is a list — consumer's list replaces.
        // (The "members:-replaces-stamped-tree" semantic from §5d is the
        // explicit list-replace.)
        using var pkg = BuildPackage(
            files: new[]
            {
                ("templates/team/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: UnitTemplate
                    name: team
                    description: x
                    members:
                      - agent: template-lead
                    """),
                ("units/engineering/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: engineering
                    description: x
                    from: team
                    members:
                      - agent: consumer-lead
                    """),
            });

        var resolved = await ParseAsync(pkg.Root);
        var output = await new TemplateResolver().ResolveAsync(
            resolved, pkg.Root, TestContext.Current.CancellationToken);

        var engineering = output.Units.Single(u => u.Name == "engineering");
        var engineeringContent = engineering.Content!;
        engineeringContent.ShouldContain("agent: consumer-lead");
        engineeringContent.ShouldNotContain("agent: template-lead");
    }

    [Fact]
    public async Task ResolveAsync_TemplateChain_ResolvesBottomUp()
    {
        // Agent → AgentTemplate b → AgentTemplate a. Outer agent ships
        // `role:`; intermediate `b` ships `instructions:`; root `a`
        // ships `description:`-equivalent fields. The fully-resolved
        // body should carry fields from every level, with the
        // innermost-declared value winning per the bottom-up chain.
        using var pkg = BuildPackage(
            files: new[]
            {
                ("templates/a/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: AgentTemplate
                    name: a
                    description: root-template
                    role: root-role
                    instructions: root-instructions
                    """),
                ("templates/b/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: AgentTemplate
                    name: b
                    description: mid-template
                    from: a
                    instructions: mid-instructions
                    """),
                ("agents/instance/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Agent
                    name: instance
                    description: x
                    from: b
                    role: instance-role
                    """),
            });

        var resolved = await ParseAsync(pkg.Root);
        var output = await new TemplateResolver().ResolveAsync(
            resolved, pkg.Root, TestContext.Current.CancellationToken);

        var instance = output.Agents.Single(a => a.Name == "instance");
        var instanceContent = instance.Content!;
        // Consumer wins over template b which wins over template a.
        instanceContent.ShouldContain("role: instance-role");
        instanceContent.ShouldContain("instructions: mid-instructions");
    }

    [Fact]
    public async Task ResolveAsync_TemplateChildrenCloned_AsFreshChildrenOfConsumer()
    {
        // The template `team` ships two concrete child agents under
        // `templates/team/agents/`. Three instances of `team` should
        // produce three distinct cloned agents each. v0.1 cloning
        // preserves the template's child names; they share display
        // names across instances and identity is Guid (ADR-0036) —
        // which is minted downstream by the install pipeline, not at
        // this layer.
        using var pkg = BuildPackage(
            files: new[]
            {
                ("templates/team/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: UnitTemplate
                    name: team
                    description: x
                    """),
                ("templates/team/agents/team-lead/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Agent
                    name: team-lead
                    description: lead
                    """),
                ("templates/team/agents/team-engineer/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Agent
                    name: team-engineer
                    description: eng
                    """),
                ("units/eng-team/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: eng-team
                    description: x
                    from: team
                    """),
            });

        var resolved = await ParseAsync(pkg.Root);
        var output = await new TemplateResolver().ResolveAsync(
            resolved, pkg.Root, TestContext.Current.CancellationToken);

        // The consumer unit shows up.
        output.Units.Where(u => u.Name == "eng-team").Count().ShouldBe(1);

        // The template's two concrete children are cloned as fresh
        // concrete agents of the consumer.
        var clonedAgents = output.Agents.Where(a => a.Name == "team-lead" || a.Name == "team-engineer").ToList();
        clonedAgents.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ResolveAsync_CrossPackageFrom_LoadsFromCatalog()
    {
        // The package declares a consumer agent with
        // `from: pkg-a/external@1.0`. The catalog provider returns the
        // template body; the resolver merges it into the consumer.
        using var pkg = BuildPackage(
            files: new[]
            {
                ("agents/local/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Agent
                    name: local
                    description: x
                    from: pkg-a/external@1.0
                    role: instance-role
                    """),
            });

        var resolved = await ParseAsync(pkg.Root);

        var stubProvider = new StubCatalogProvider(
            (pkg: "pkg-a", name: "external"),
            yaml: """
                apiVersion: spring.voyage/v1
                kind: AgentTemplate
                name: external
                description: external template
                role: template-role
                instructions: from external
                """);
        var resolver = new TemplateResolver(stubProvider);

        var output = await resolver.ResolveAsync(resolved, pkg.Root, TestContext.Current.CancellationToken);

        var local = output.Agents.Single(a => a.Name == "local");
        var localContent = local.Content!;
        localContent.ShouldContain("role: instance-role");          // consumer wins
        localContent.ShouldContain("from external");                 // cross-package template fills the gap
    }

    [Fact]
    public async Task ResolveAsync_TwoInstancesOfSameTemplate_ProduceDistinctClones()
    {
        // Two units that both reference `team`; each clones the
        // template's `team-lead` child. The output has two consumer
        // units plus two cloned `team-lead` agents — identity is by
        // (parent unit + display name) which is the wire-shape the
        // install pipeline uses to mint Guids per instance.
        using var pkg = BuildPackage(
            files: new[]
            {
                ("templates/team/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: UnitTemplate
                    name: team
                    description: x
                    """),
                ("templates/team/agents/team-lead/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Agent
                    name: team-lead
                    description: lead
                    """),
                ("units/eng-team/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: eng-team
                    description: x
                    from: team
                    """),
                ("units/design-team/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: design-team
                    description: x
                    from: team
                    """),
            });

        var resolved = await ParseAsync(pkg.Root);
        var output = await new TemplateResolver().ResolveAsync(
            resolved, pkg.Root, TestContext.Current.CancellationToken);

        // Both consumer units present.
        output.Units.Count(u => u.Name == "eng-team").ShouldBe(1);
        output.Units.Count(u => u.Name == "design-team").ShouldBe(1);
        // Two stamped clones — one per consumer unit. Same display
        // name; identity minted later by install pipeline.
        output.Agents.Count(a => a.Name == "team-lead").ShouldBe(2);
    }

    [Fact]
    public async Task ResolveAsync_CrossPackageFrom_WithOneNestedAgent_StampsChild()
    {
        // ADR-0043 §5h archetype-library case: the cross-package template
        // ships ONE nested concrete agent. The consumer instance gets the
        // nested agent stamped fresh into its own tree.
        using var pkg = BuildPackage(
            files: new[]
            {
                ("units/local-team/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: local-team
                    description: x
                    from: pkg-a/team-template@1.0
                    """),
            });

        var resolved = await ParseAsync(pkg.Root);

        var stubProvider = new MultiKeyStubCatalogProvider();
        stubProvider.AddArtefact("pkg-a", ArtefactKind.Unit, "team-template", """
            apiVersion: spring.voyage/v1
            kind: UnitTemplate
            name: team-template
            description: cross-pkg team archetype
            """);
        stubProvider.AddNested("pkg-a", ArtefactKind.Unit, "team-template", new[]
        {
            new NestedArtefactDescriptor(
                Kind: ArtefactKind.Agent,
                Name: "external-lead",
                Yaml: """
                    apiVersion: spring.voyage/v1
                    kind: Agent
                    name: external-lead
                    description: lead from the cross-package archetype
                    """,
                ContainingArtefactName: null),
        });

        var resolver = new TemplateResolver(stubProvider);
        var output = await resolver.ResolveAsync(resolved, pkg.Root, TestContext.Current.CancellationToken);

        output.Units.Count(u => u.Name == "local-team").ShouldBe(1);
        output.Agents.Count(a => a.Name == "external-lead").ShouldBe(1);

        // The cloned child is owned by the consumer unit, not by the
        // archetype library — its ContainingArtefactName has been
        // rebound to the consumer.
        var lead = output.Agents.Single(a => a.Name == "external-lead");
        lead.ContainingArtefactName.ShouldBe("local-team");
        lead.IsCrossPackage.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_CrossPackageFrom_WithTwoNestedAgentsUnderSubUnit_StampsFullTree()
    {
        // ADR-0043 §5h: the cross-package template ships a nested
        // sub-unit that owns two agents. The consumer should receive
        // the full subtree — one sub-unit plus its two agents.
        using var pkg = BuildPackage(
            files: new[]
            {
                ("units/local-org/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: local-org
                    description: x
                    from: pkg-b/org-template@1.0
                    """),
            });

        var resolved = await ParseAsync(pkg.Root);

        var stubProvider = new MultiKeyStubCatalogProvider();
        stubProvider.AddArtefact("pkg-b", ArtefactKind.Unit, "org-template", """
            apiVersion: spring.voyage/v1
            kind: UnitTemplate
            name: org-template
            description: x
            """);
        stubProvider.AddNested("pkg-b", ArtefactKind.Unit, "org-template", new[]
        {
            new NestedArtefactDescriptor(
                Kind: ArtefactKind.Unit,
                Name: "sub-team",
                Yaml: """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: sub-team
                    description: nested sub-unit
                    """,
                ContainingArtefactName: null),
            new NestedArtefactDescriptor(
                Kind: ArtefactKind.Agent,
                Name: "alpha",
                Yaml: """
                    apiVersion: spring.voyage/v1
                    kind: Agent
                    name: alpha
                    description: agent under sub-team
                    """,
                ContainingArtefactName: "sub-team"),
            new NestedArtefactDescriptor(
                Kind: ArtefactKind.Agent,
                Name: "beta",
                Yaml: """
                    apiVersion: spring.voyage/v1
                    kind: Agent
                    name: beta
                    description: agent under sub-team
                    """,
                ContainingArtefactName: "sub-team"),
        });

        var resolver = new TemplateResolver(stubProvider);
        var output = await resolver.ResolveAsync(resolved, pkg.Root, TestContext.Current.CancellationToken);

        // Consumer + one cloned sub-unit + two cloned agents.
        output.Units.Count(u => u.Name == "local-org").ShouldBe(1);
        output.Units.Count(u => u.Name == "sub-team").ShouldBe(1);
        output.Agents.Count(a => a.Name == "alpha").ShouldBe(1);
        output.Agents.Count(a => a.Name == "beta").ShouldBe(1);

        // Containment chain stays honest: agents are owned by sub-team;
        // sub-team is owned by the consumer unit.
        output.Agents.Single(a => a.Name == "alpha").ContainingArtefactName.ShouldBe("sub-team");
        output.Agents.Single(a => a.Name == "beta").ContainingArtefactName.ShouldBe("sub-team");
        output.Units.Single(u => u.Name == "sub-team").ContainingArtefactName.ShouldBe("local-org");
    }

    [Fact]
    public async Task ResolveAsync_CrossPackageFrom_WithChainedFromAcrossPackages_ResolvesTransitively()
    {
        // ADR-0043 §5e + §5h: a cross-package template whose nested
        // child itself declares `from: <other-pkg>/<other-template>`
        // resolves transitively — the consumer sees the chained
        // template's body merged into the nested child.
        using var pkg = BuildPackage(
            files: new[]
            {
                ("units/local-org/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: local-org
                    description: x
                    from: pkg-c/team-template@1.0
                    """),
            });

        var resolved = await ParseAsync(pkg.Root);

        var stubProvider = new MultiKeyStubCatalogProvider();
        // Outer cross-package template body.
        stubProvider.AddArtefact("pkg-c", ArtefactKind.Unit, "team-template", """
            apiVersion: spring.voyage/v1
            kind: UnitTemplate
            name: team-template
            description: x
            """);
        // Nested child carries its own cross-package `from:` →
        // another package's archetype template body.
        stubProvider.AddNested("pkg-c", ArtefactKind.Unit, "team-template", new[]
        {
            new NestedArtefactDescriptor(
                Kind: ArtefactKind.Agent,
                Name: "engineer",
                Yaml: """
                    apiVersion: spring.voyage/v1
                    kind: Agent
                    name: engineer
                    description: x
                    from: pkg-d/engineer-archetype@1.0
                    """,
                ContainingArtefactName: null),
        });
        // Far-side archetype template.
        stubProvider.AddArtefact("pkg-d", ArtefactKind.Agent, "engineer-archetype", """
            apiVersion: spring.voyage/v1
            kind: AgentTemplate
            name: engineer-archetype
            description: x
            role: senior-engineer
            instructions: |
                Far-side archetype instructions.
            """);

        var resolver = new TemplateResolver(stubProvider);
        var output = await resolver.ResolveAsync(resolved, pkg.Root, TestContext.Current.CancellationToken);

        var engineer = output.Agents.Single(a => a.Name == "engineer");
        engineer.Content.ShouldNotBeNull();
        var engineerContent = engineer.Content!;
        // Far-side archetype body flows through into the cloned child.
        engineerContent.ShouldContain("role: senior-engineer");
        engineerContent.ShouldContain("Far-side archetype instructions.");
    }

    [Fact]
    public async Task ResolveAsync_MissingTemplate_RaisesParseException()
    {
        using var pkg = BuildPackage(
            files: new[]
            {
                ("agents/local/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Agent
                    name: local
                    description: x
                    from: not-a-template
                    """),
            });

        var resolved = await ParseAsync(pkg.Root);
        await Should.ThrowAsync<PackageParseException>(async () =>
            await new TemplateResolver().ResolveAsync(
                resolved, pkg.Root, TestContext.Current.CancellationToken));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private const string PackageHeaderYaml = """
        apiVersion: spring.voyage/v1
        kind: Package
        name: my-package
        description: A resolver-test package.
        version: 1.0.0
        """;

    private static async Task<ResolvedPackage> ParseAsync(string root)
    {
        var yaml = await File.ReadAllTextAsync(Path.Combine(root, "package.yaml"));
        return await PackageManifestParser.ParseAndResolveAsync(yaml, root);
    }

    private static TempPackage BuildPackage((string Path, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "sv-tpl-" + Path.GetRandomFileName());
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

    private sealed class StubCatalogProvider : IPackageCatalogProvider
    {
        private readonly (string pkg, string name) _key;
        private readonly string _yaml;

        public StubCatalogProvider((string pkg, string name) key, string yaml)
        {
            _key = key;
            _yaml = yaml;
        }

        public Task<bool> PackageExistsAsync(string packageName, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(packageName, _key.pkg, StringComparison.Ordinal));

        public Task<string?> LoadArtefactYamlAsync(
            string packageName,
            ArtefactKind kind,
            string artefactName,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(packageName, _key.pkg, StringComparison.Ordinal)
                && string.Equals(artefactName, _key.name, StringComparison.Ordinal))
            {
                return Task.FromResult<string?>(_yaml);
            }
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// Test catalog provider that supports multiple (package, kind, name)
    /// keys and per-template nested-child enumeration. Backs the
    /// cross-package archetype-library tests for ADR-0043 §5h.
    /// </summary>
    private sealed class MultiKeyStubCatalogProvider : IPackageCatalogProvider
    {
        private readonly Dictionary<string, string> _artefacts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<NestedArtefactDescriptor>> _nested =
            new(StringComparer.Ordinal);

        public void AddArtefact(string packageName, ArtefactKind kind, string artefactName, string yaml)
        {
            _artefacts[Key(packageName, kind, artefactName)] = yaml;
        }

        public void AddNested(string packageName, ArtefactKind parentKind, string parentName,
            IReadOnlyList<NestedArtefactDescriptor> descriptors)
        {
            _nested[Key(packageName, parentKind, parentName)] = descriptors;
        }

        public Task<bool> PackageExistsAsync(string packageName, CancellationToken cancellationToken = default)
        {
            foreach (var key in _artefacts.Keys)
            {
                if (key.StartsWith(packageName + "|", StringComparison.Ordinal))
                {
                    return Task.FromResult(true);
                }
            }
            return Task.FromResult(false);
        }

        public Task<string?> LoadArtefactYamlAsync(
            string packageName,
            ArtefactKind kind,
            string artefactName,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(
                _artefacts.TryGetValue(Key(packageName, kind, artefactName), out var yaml)
                    ? yaml
                    : null);
        }

        public Task<IReadOnlyList<NestedArtefactDescriptor>> EnumerateNestedArtefactsAsync(
            string packageName,
            ArtefactKind parentKind,
            string parentArtefactName,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                _nested.TryGetValue(Key(packageName, parentKind, parentArtefactName), out var nested)
                    ? nested
                    : (IReadOnlyList<NestedArtefactDescriptor>)Array.Empty<NestedArtefactDescriptor>());
        }

        private static string Key(string packageName, ArtefactKind kind, string name)
            => $"{packageName}|{kind}|{name}";
    }
}
