// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Artefacts;

using Shouldly;

using Xunit;

/// <summary>
/// Parser tests for ADR-0046 — the unified members grammar that promotes
/// <c>human</c> to a peer of <c>agent</c> / <c>unit</c> under a unit's
/// <c>members:</c> list, introduces the <see cref="HumanTemplateManifest"/>
/// kind alongside the existing template kinds, and drops the legacy
/// top-level <c>humans:</c> block plus the <c>workflows/</c> /
/// <c>connectors/</c> conventional subdirectories. The tests cover both the
/// happy-path manifest shapes (<see cref="ManifestParser.Parse"/>,
/// <see cref="ManifestParser.ParseHumanTemplate"/>, end-to-end resolution
/// through <see cref="PackageManifestParser.ParseAndResolveAsync"/>) and the
/// structured-error surfaces the parser raises for every legacy signal.
/// </summary>
public class UnifiedMembersGrammarTests
{
    // ── ADR-0046 §1: `- human:` discriminator under `members:` ───────────

    [Fact]
    public void ParseUnit_InlineHumanMember_ParsesPopulatedHumanManifest()
    {
        // The inline body carries the multi-valued roles / expertise /
        // notifications lists per ADR-0046 §3. After parsing, the typed
        // HumanManifest projection should round-trip every field declared
        // on the inline body.
        const string yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: engineering
            description: x
            members:
              - human:
                  displayName: Alice
                  roles: [foo, bar]
                  expertise: [security]
                  notifications: [escalation]
            """;

        var manifest = ManifestParser.Parse(yaml);

        manifest.Members.ShouldNotBeNull();
        manifest.Members!.Count.ShouldBe(1);

        var member = manifest.Members[0];
        member.Human.ShouldNotBeNull();
        member.Human!.IsInline.ShouldBeTrue();

        // The inline body's `name:` is the local symbol, but humans are
        // exempt from the "missing name" check (their identity is server-
        // allocated). When the body declares displayName: instead, the
        // helper falls back to the converter's `<inline>` placeholder.
        member.HumanName.ShouldBe("<inline>");

        // Re-deserialise the captured inline body into the typed
        // HumanManifest so the test asserts against the same shape the
        // install-time reader consumes.
        var human = DeserializeHuman(member.Human.InlineBody!);
        human.DisplayName.ShouldBe("Alice");
        human.Roles.ShouldBe(new[] { "foo", "bar" });
        human.Expertise.ShouldBe(new[] { "security" });
        human.Notifications.ShouldBe(new[] { "escalation" });
    }

    // ── ADR-0046 §4: HumanTemplate stamping via `- human: { from: X }` ───

    [Fact]
    public async Task ParseAndResolve_InlineHumanMember_FromHumanTemplate_StampsRolesAndExpertise()
    {
        // A package ships a `HumanTemplate` carrying default roles +
        // expertise; a unit's `- human: { from: ... }` member refers to it
        // by bare name. The resolved package should expose both the
        // template (under HumanTemplates) and let the inline body's
        // captured `from:` route through unchanged so the install reader
        // can stamp a concrete row.
        var packageRoot = await BuildPackageAsync(
            ("templates/security-lead/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: HumanTemplate
                name: security-lead
                description: Security lead human archetype.
                roles: [security-lead]
                expertise: [crypto, secrets]
                """),
            ("units/engineering/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: Unit
                name: engineering
                description: x
                members:
                  - human: { from: security-lead }
                """));

        try
        {
            var yaml = await File.ReadAllTextAsync(
                Path.Combine(packageRoot, "package.yaml"),
                TestContext.Current.CancellationToken);
            var resolved = await PackageManifestParser.ParseAndResolveAsync(
                yaml, packageRoot,
                cancellationToken: TestContext.Current.CancellationToken);

            // The template surfaces as a peer artefact under HumanTemplates.
            resolved.HumanTemplates.Count.ShouldBe(1);
            var templateArtefact = resolved.HumanTemplates[0];
            templateArtefact.Name.ShouldBe("security-lead");
            templateArtefact.Kind.ShouldBe(ArtefactKind.HumanTemplate);

            // Parsing the template's raw YAML through ParseHumanTemplate
            // round-trips the roles / expertise the template author wrote.
            var template = ManifestParser.ParseHumanTemplate(templateArtefact.Content!);
            template.Roles.ShouldBe(new[] { "security-lead" });
            template.Expertise.ShouldBe(new[] { "crypto", "secrets" });

            // The unit's inline-human member retains its `from:` reference
            // through resolution. Parse the unit's content back through
            // ManifestParser.Parse so the test asserts against the same
            // shape the install reader consumes.
            var unitArtefact = resolved.Units.Single(u => u.Name == "engineering");
            var unit = ManifestParser.Parse(unitArtefact.Content!);
            unit.Members!.Count.ShouldBe(1);
            var humanBody = DeserializeHuman(unit.Members[0].Human!.InlineBody!);
            humanBody.From.ShouldBe("security-lead");
        }
        finally
        {
            CleanupPackage(packageRoot);
        }
    }

    [Fact]
    public async Task ParseAndResolve_HumanTemplate_MemberRoles_FullyReplaceTemplateRoles()
    {
        // ADR-0046 §5: list fields (roles, expertise, notifications) follow
        // full-replacement semantics when the member entry declares them.
        // The captured inline body carries the override verbatim; the test
        // verifies the wire shape — the install reader performs the actual
        // merge but it can only do so when the parser preserves the member
        // entry's overrides intact.
        var packageRoot = await BuildPackageAsync(
            ("templates/security-lead/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: HumanTemplate
                name: security-lead
                description: Security lead human archetype.
                roles: [security-lead]
                expertise: [crypto, secrets]
                """),
            ("units/engineering/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: Unit
                name: engineering
                description: x
                members:
                  - human:
                      from: security-lead
                      roles: [crypto-lead]
                      expertise: [audits]
                """));

        try
        {
            var yaml = await File.ReadAllTextAsync(
                Path.Combine(packageRoot, "package.yaml"),
                TestContext.Current.CancellationToken);
            var resolved = await PackageManifestParser.ParseAndResolveAsync(
                yaml, packageRoot,
                cancellationToken: TestContext.Current.CancellationToken);

            var unitArtefact = resolved.Units.Single(u => u.Name == "engineering");
            var unit = ManifestParser.Parse(unitArtefact.Content!);
            var humanBody = DeserializeHuman(unit.Members![0].Human!.InlineBody!);

            // The member entry's roles fully replace the template's roles
            // — they do NOT union with `[security-lead]`. Same for expertise.
            humanBody.From.ShouldBe("security-lead");
            humanBody.Roles.ShouldBe(new[] { "crypto-lead" });
            humanBody.Expertise.ShouldBe(new[] { "audits" });
        }
        finally
        {
            CleanupPackage(packageRoot);
        }
    }

    // ── ADR-0046 §4: HumanTemplate kind discrimination ───────────────────

    [Fact]
    public async Task ParseAndResolve_TemplatesDir_MixedKinds_RoutesHumanTemplate()
    {
        // The templates/ subdirectory hosts AgentTemplate / UnitTemplate /
        // HumanTemplate side by side; the inner `kind:` field
        // disambiguates. The HumanTemplate artefact must land on
        // ResolvedPackage.HumanTemplates (and not on Units / Agents).
        var packageRoot = await BuildPackageAsync(
            ("templates/engineer/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: AgentTemplate
                name: engineer
                description: engineer archetype
                """),
            ("templates/engineering-team/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: UnitTemplate
                name: engineering-team
                description: engineering team archetype
                """),
            ("templates/oss-operator/package.yaml", """
                apiVersion: spring.voyage/v1
                kind: HumanTemplate
                name: oss-operator
                description: oss operator archetype
                roles: [owner]
                """));

        try
        {
            var yaml = await File.ReadAllTextAsync(
                Path.Combine(packageRoot, "package.yaml"),
                TestContext.Current.CancellationToken);
            var resolved = await PackageManifestParser.ParseAndResolveAsync(
                yaml, packageRoot,
                cancellationToken: TestContext.Current.CancellationToken);

            resolved.HumanTemplates.Count.ShouldBe(1);
            resolved.HumanTemplates[0].Name.ShouldBe("oss-operator");
            resolved.HumanTemplates[0].Kind.ShouldBe(ArtefactKind.HumanTemplate);

            // The other template kinds project onto their concrete kind
            // (Unit / Agent) for indexing per PackageManifestParser.MapKind.
            resolved.Agents.Any(a => a.Name == "engineer").ShouldBeTrue();
            resolved.Units.Any(u => u.Name == "engineering-team").ShouldBeTrue();
        }
        finally
        {
            CleanupPackage(packageRoot);
        }
    }

    // ── ADR-0046 §1: LegacyHumansBlock rejection ─────────────────────────

    [Fact]
    public void Parse_LegacyHumansBlock_RaisesStructuredError()
    {
        // The legacy top-level `humans:` slot is gone; each participant is
        // declared under members: with a `- human:` entry. The parser
        // surfaces a structured error pointing at ADR-0046 §1.
        const string yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: legacy-unit
            description: x
            humans:
              - role: owner
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));
        ex.Message.ShouldContain("LegacyHumansBlock");
        ex.Message.ShouldContain("ADR-0046 §1");
    }

    [Fact]
    public void ParseUnitTemplate_LegacyHumansBlock_RaisesStructuredError()
    {
        // The same legacy `humans:` slot is rejected on UnitTemplate
        // documents — the migration hint applies uniformly.
        const string yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitTemplate
            name: legacy-template
            description: x
            humans:
              - role: owner
            """;

        var ex = Should.Throw<ManifestParseException>(
            () => ManifestParser.ParseUnitTemplate(yaml));
        ex.Message.ShouldContain("LegacyHumansBlock");
        ex.Message.ShouldContain("ADR-0046 §1");
    }

    // ── ADR-0046 §2: workflows/ subdir rejection (top-level + nested) ────

    [Fact]
    public void Walk_TopLevelWorkflowsSubdir_Rejected()
    {
        using var pkg = BuildPackageRaw(rootYaml: PackageHeaderYaml,
            entries: new[]
            {
                ("workflows/foo/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Workflow
                    name: foo
                    description: x
                    """),
            });

        var ex = Should.Throw<PackageParseException>(
            () => PackageManifestParser.Walk(pkg.Root, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("LegacyWorkflowsSubdir");
        ex.Message.ShouldContain("ADR-0046 §2");
    }

    [Fact]
    public void Walk_NestedWorkflowsSubdir_Rejected()
    {
        // The rejection fires at any depth — a workflows/ folder buried
        // inside a unit's nested layout still surfaces the migration hint.
        using var pkg = BuildPackageRaw(rootYaml: PackageHeaderYaml,
            entries: new[]
            {
                ("units/engineering/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: engineering
                    description: x
                    """),
                ("units/engineering/workflows/legacy/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Workflow
                    name: legacy
                    description: x
                    """),
            });

        var ex = Should.Throw<PackageParseException>(
            () => PackageManifestParser.Walk(pkg.Root, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("LegacyWorkflowsSubdir");
    }

    // ── ADR-0046 §2: connectors/ subdir rejection (top-level + nested) ───

    [Fact]
    public void Walk_TopLevelConnectorsSubdir_Rejected()
    {
        using var pkg = BuildPackageRaw(rootYaml: PackageHeaderYaml,
            entries: new[]
            {
                ("connectors/github/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Connector
                    name: github
                    description: x
                    """),
            });

        var ex = Should.Throw<PackageParseException>(
            () => PackageManifestParser.Walk(pkg.Root, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("LegacyConnectorsSubdir");
        ex.Message.ShouldContain("ADR-0046 §2");
    }

    [Fact]
    public void Walk_NestedConnectorsSubdir_Rejected()
    {
        using var pkg = BuildPackageRaw(rootYaml: PackageHeaderYaml,
            entries: new[]
            {
                ("units/engineering/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: engineering
                    description: x
                    """),
                ("units/engineering/connectors/github/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Connector
                    name: github
                    description: x
                    """),
            });

        var ex = Should.Throw<PackageParseException>(
            () => PackageManifestParser.Walk(pkg.Root, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("LegacyConnectorsSubdir");
    }

    // ── ADR-0046 §2: LegacyWorkflowKind rejection ────────────────────────

    [Fact]
    public void Walk_LegacyWorkflowKind_Rejected()
    {
        // A YAML document declaring `kind: Workflow` under any
        // conventional subdirectory raises the explicit
        // LegacyWorkflowKind error rather than falling through to the
        // generic "unknown kind" branch. We lodge the document under
        // templates/ to exercise the path that doesn't fire the
        // workflows/ early-reject (so the kind-check branch runs).
        using var pkg = BuildPackageRaw(rootYaml: PackageHeaderYaml,
            entries: new[]
            {
                ("templates/legacy/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Workflow
                    name: legacy
                    description: x
                    """),
            });

        var ex = Should.Throw<PackageParseException>(
            () => PackageManifestParser.Walk(pkg.Root, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("LegacyWorkflowKind");
        ex.Message.ShouldContain("ADR-0046 §2");
    }

    // ── ADR-0046 §2: ConventionalSubdirs vocabulary trim ─────────────────

    [Fact]
    public void ConventionalSubdirs_DoesNotContainWorkflowsOrConnectors()
    {
        // The catalog walker's vocabulary trim is part of the ADR — assert
        // against the internal table directly so a future regression that
        // re-adds either subdir is caught at the lowest layer, not the
        // walker-integration layer.
        PackageManifestParser.ConventionalSubdirs.Keys.ShouldNotContain("workflows");
        PackageManifestParser.ConventionalSubdirs.Keys.ShouldNotContain("connectors");
    }

    [Fact]
    public void ConventionalSubdirs_TemplatesMapsToUnitAgentAndHumanTemplate()
    {
        // templates/ hosts three kinds side by side (ADR-0043 §5b + ADR-
        // 0045 §4): UnitTemplate / AgentTemplate / HumanTemplate. The
        // internal kind-list drives the walker's allowedKinds check.
        PackageManifestParser.ConventionalSubdirs.ContainsKey("templates").ShouldBeTrue();
        var kinds = PackageManifestParser.ConventionalSubdirs["templates"];
        kinds.ShouldContain(ArtefactKind.Unit);
        kinds.ShouldContain(ArtefactKind.Agent);
        kinds.ShouldContain(ArtefactKind.HumanTemplate);
    }

    // ── ADR-0046 §3: multi-valued roles parameterisation ─────────────────

    [Theory]
    [InlineData("roles: []", 0)]
    [InlineData("roles: [single]", 1)]
    [InlineData("roles: [a, b]", 2)]
    [InlineData("roles: [a, b, c]", 3)]
    public void ParseUnit_InlineHumanRoles_ParsesAsList(string rolesYaml, int expectedCount)
    {
        // ADR-0046 §3: roles are multi-valued; the parser preserves the
        // author's exact tokens — case-insensitive uniqueness within the
        // entry is a runtime / UI concern, not a parse-time rule.
        var yaml = $"""
            apiVersion: spring.voyage/v1
            kind: Unit
            name: engineering
            description: x
            members:
              - human:
                  {rolesYaml}
            """;

        var manifest = ManifestParser.Parse(yaml);

        manifest.Members.ShouldNotBeNull();
        manifest.Members!.Count.ShouldBe(1);

        var human = DeserializeHuman(manifest.Members[0].Human!.InlineBody!);

        if (expectedCount == 0)
        {
            // Empty list and absent field are equivalent at the manifest
            // layer — YamlDotNet deserialises `roles: []` to an empty list
            // (or null, depending on the YAML emitter that captured the
            // inline body). Both shapes carry zero roles.
            (human.Roles?.Count ?? 0).ShouldBe(0);
        }
        else
        {
            human.Roles.ShouldNotBeNull();
            human.Roles!.Count.ShouldBe(expectedCount);
        }
    }

    [Fact]
    public void ParseUnit_InlineHumanRoles_DuplicatesNotEnforcedAtParseTime()
    {
        // The parser preserves whatever the author wrote — `[admin, admin]`
        // round-trips as a two-element list. Dedup is a runtime / UI
        // concern (the install reader / portal); the parser is intentionally
        // lossless here so a future per-tenant policy can decide the
        // canonical form without re-reading the YAML.
        const string yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: engineering
            description: x
            members:
              - human:
                  roles: [admin, Admin, ADMIN]
            """;

        var manifest = ManifestParser.Parse(yaml);

        var human = DeserializeHuman(manifest.Members![0].Human!.InlineBody!);
        human.Roles.ShouldBe(new[] { "admin", "Admin", "ADMIN" });
    }

    // ── HumanTemplate kind parsing through ManifestParser.ParseHumanTemplate ──

    [Fact]
    public void ParseHumanTemplate_HappyPath_ReturnsTypedManifest()
    {
        // The per-kind parser entrypoint for HumanTemplate documents
        // mirrors AgentTemplate / UnitTemplate. Strict parsing — every
        // declared field round-trips onto the typed projection.
        const string yaml = """
            apiVersion: spring.voyage/v1
            kind: HumanTemplate
            name: oss-operator
            description: oss operator archetype
            displayName: Operator
            roles: [owner, reviewer]
            expertise: [release-mgmt]
            notifications: [escalation]
            """;

        var manifest = ManifestParser.ParseHumanTemplate(yaml);

        manifest.Kind.ShouldBe("HumanTemplate");
        manifest.Name.ShouldBe("oss-operator");
        manifest.Description.ShouldBe("oss operator archetype");
        manifest.DisplayName.ShouldBe("Operator");
        manifest.Roles.ShouldBe(new[] { "owner", "reviewer" });
        manifest.Expertise.ShouldBe(new[] { "release-mgmt" });
        manifest.Notifications.ShouldBe(new[] { "escalation" });
    }

    [Fact]
    public void ParseHumanTemplate_MismatchedKind_Rejected()
    {
        // A document declaring `kind: HumanTemplate` is required by the
        // entrypoint — passing a `kind: UnitTemplate` (or any other kind)
        // through ParseHumanTemplate surfaces a precise error.
        const string yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitTemplate
            name: not-a-human-template
            description: x
            """;

        var ex = Should.Throw<ManifestParseException>(
            () => ManifestParser.ParseHumanTemplate(yaml));
        ex.Message.ShouldContain("HumanTemplate");
    }

    // ── Test helpers ─────────────────────────────────────────────────────

    private const string PackageHeaderYaml = """
        apiVersion: spring.voyage/v1
        kind: Package
        name: unified-members-test
        description: A unified-members-grammar test package.
        version: 1.0.0
        """;

    /// <summary>
    /// Re-deserialises a captured inline-body YAML mapping into the typed
    /// <see cref="HumanManifest"/> projection so tests can assert against
    /// the same shape the install-time reader consumes after stamping.
    /// </summary>
    private static HumanManifest DeserializeHuman(string inlineBody)
    {
        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<HumanManifest>(inlineBody) ?? new HumanManifest();
    }

    /// <summary>
    /// Lays down a package tree on a temporary directory and writes the
    /// package's root <c>package.yaml</c>. The caller is responsible for
    /// invoking <see cref="CleanupPackage"/> in a finally clause; the
    /// builder is async so it composes naturally with the
    /// <see cref="PackageManifestParser.ParseAndResolveAsync"/> entry point.
    /// </summary>
    private static async Task<string> BuildPackageAsync(params (string Path, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "sv-unified-" + Path.GetRandomFileName());
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

    private static void CleanupPackage(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Lays down a package tree where every entry's content is provided
    /// verbatim. Mirrors the helper in <see cref="Adr0043WalkerTests"/> so
    /// the structured-error tests can co-locate with the recursive-walker
    /// fixtures' style.
    /// </summary>
    private static TempPackage BuildPackageRaw(
        string rootYaml,
        (string RelativePath, string Content)[] entries)
    {
        var root = Path.Combine(Path.GetTempPath(), "sv-unified-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.yaml"), rootYaml);
        foreach (var (rel, content) in entries)
        {
            var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        return new TempPackage(root);
    }

    private sealed class TempPackage : System.IDisposable
    {
        public string Root { get; }

        public TempPackage(string root)
        {
            Root = root;
        }

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
