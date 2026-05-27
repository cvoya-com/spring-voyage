// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Lifecycle;

using Shouldly;

using Xunit;

/// <summary>
/// Parser tests for ADR-0043 chunk 2 (recursive catalog walker, legacy-error
/// activation, cycle detection). Covers:
/// <list type="bullet">
///   <item><description>§2 — the catalog walker enumerates artefact folders under each conventional subdirectory.</description></item>
///   <item><description>§3 — artefacts compose recursively: a unit can ship its own agents/, etc.</description></item>
///   <item><description>§4 — inner artefact <c>package.yaml</c> files must not declare <c>version:</c>.</description></item>
///   <item><description>§8 — every legacy-shape signal raises the matching <c>Adr0043ParseErrors</c> code.</description></item>
///   <item><description>§7 — cycle detection across <c>members:</c>, <c>from:</c>, and containment.</description></item>
/// </list>
/// </summary>
public class Adr0043WalkerTests
{
    // ── Walker happy-path ────────────────────────────────────────────────

    [Fact]
    public void Walk_FlatUnitsAndAgents_DiscoversTopLevelArtefacts()
    {
        using var pkg = BuildPackage(rootYaml: PackageHeaderYaml,
            artefacts: new[]
            {
                ("units/alpha", "Unit", "alpha"),
                ("units/beta", "Unit", "beta"),
                ("agents/worker", "Agent", "worker"),
            });

        var discovered = PackageManifestParser.Walk(pkg.Root, TestContext.Current.CancellationToken);

        discovered.Select(d => (d.Kind, d.Name)).ShouldBe(
            new[]
            {
                (ArtefactKind.Unit, "alpha"),
                (ArtefactKind.Unit, "beta"),
                (ArtefactKind.Agent, "worker"),
            },
            ignoreOrder: true);
    }

    [Fact]
    public void Walk_NestedAgentsUnderUnit_DiscoveredRecursively()
    {
        // ADR-0043 §3: a Unit folder can contain its own agents/ — the
        // walker descends every conventional subdirectory at every depth.
        using var pkg = BuildPackage(rootYaml: PackageHeaderYaml,
            artefacts: new[]
            {
                ("units/eng", "Unit", "eng"),
                ("units/eng/agents/lead", "Agent", "lead"),
                ("units/eng/agents/dev", "Agent", "dev"),
            });

        var discovered = PackageManifestParser.Walk(pkg.Root, TestContext.Current.CancellationToken);

        discovered.Select(d => (d.Kind, d.Name)).ShouldBe(
            new[]
            {
                (ArtefactKind.Unit, "eng"),
                (ArtefactKind.Agent, "lead"),
                (ArtefactKind.Agent, "dev"),
            },
            ignoreOrder: true);
    }

    [Fact]
    public void Walk_TemplatesSubdir_DiscoversBothKinds()
    {
        // ADR-0043 §5b: templates/ holds both UnitTemplate and
        // AgentTemplate folders side by side; the inner kind: disambiguates.
        using var pkg = BuildPackage(rootYaml: PackageHeaderYaml,
            artefacts: new[]
            {
                ("templates/engineer", "AgentTemplate", "engineer"),
                ("templates/engineering-team", "UnitTemplate", "engineering-team"),
            });

        var discovered = PackageManifestParser.Walk(pkg.Root, TestContext.Current.CancellationToken);

        var byName = discovered.ToDictionary(d => d.Name, d => d.Kind);
        byName["engineer"].ShouldBe(ArtefactKind.Agent);
        byName["engineering-team"].ShouldBe(ArtefactKind.Unit);
    }

    [Fact]
    public void Walk_IgnoresNonConventionalDirectories()
    {
        using var pkg = BuildPackage(rootYaml: PackageHeaderYaml,
            artefacts: new[]
            {
                ("units/alpha", "Unit", "alpha"),
            });
        // Add a couple of non-conventional directories with random files.
        Directory.CreateDirectory(Path.Combine(pkg.Root, "docs"));
        File.WriteAllText(Path.Combine(pkg.Root, "docs", "intro.md"), "# Hello");
        Directory.CreateDirectory(Path.Combine(pkg.Root, "examples"));
        File.WriteAllText(Path.Combine(pkg.Root, "examples", "demo.yaml"), "name: ignored");

        var discovered = PackageManifestParser.Walk(pkg.Root, TestContext.Current.CancellationToken);

        discovered.Count.ShouldBe(1);
        discovered[0].Name.ShouldBe("alpha");
    }

    // ── Legacy flat layout rejection (LegacyFlatArtefactLayout) ──────────

    [Fact]
    public void Walk_LegacyFlatUnitYaml_Rejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "sv-walker-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "package.yaml"), PackageHeaderYaml);
            var units = Path.Combine(root, "units");
            Directory.CreateDirectory(units);
            File.WriteAllText(Path.Combine(units, "alpha.yaml"),
                "apiVersion: spring.voyage/v1\nkind: Unit\nname: alpha\ndescription: x\n");

            var ex = Should.Throw<PackageParseException>(() => PackageManifestParser.Walk(root));
            ex.Message.ShouldContain("LegacyFlatArtefactLayout");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── Inner-version rejection (UnexpectedInnerVersion) ─────────────────

    [Fact]
    public void Walk_InnerArtefactDeclaresVersion_Rejected()
    {
        using var pkg = BuildPackageRaw(rootYaml: PackageHeaderYaml,
            entries: new[]
            {
                ("units/alpha/package.yaml",
                    "apiVersion: spring.voyage/v1\nkind: Unit\nname: alpha\nversion: 9.9.9\ndescription: x\n"),
            });

        var ex = Should.Throw<PackageParseException>(() => PackageManifestParser.Walk(pkg.Root, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("UnexpectedInnerVersion");
    }

    // ── Folder-name / name: mismatch (ArtefactFolderNameMismatch) ────────

    [Fact]
    public void Walk_FolderNameMismatchesNameField_Rejected()
    {
        using var pkg = BuildPackageRaw(rootYaml: PackageHeaderYaml,
            entries: new[]
            {
                ("units/expected/package.yaml",
                    "apiVersion: spring.voyage/v1\nkind: Unit\nname: actual\ndescription: x\n"),
            });

        var ex = Should.Throw<PackageParseException>(() => PackageManifestParser.Walk(pkg.Root, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("ArtefactFolderNameMismatch");
    }

    // ── Kind / subdir mismatch ────────────────────────────────────────────

    [Fact]
    public void Walk_AgentUnderUnitsSubdir_Rejected()
    {
        using var pkg = BuildPackageRaw(rootYaml: PackageHeaderYaml,
            entries: new[]
            {
                ("units/misplaced/package.yaml",
                    "apiVersion: spring.voyage/v1\nkind: Agent\nname: misplaced\ndescription: x\n"),
            });

        var ex = Should.Throw<PackageParseException>(() => PackageManifestParser.Walk(pkg.Root, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("expects kind 'Unit'");
    }

    // ── Strict parsing — unknown ai.* sub-fields rejected (#2406) ────────

    [Fact]
    public void ManifestParser_UnknownAiSubField_Rejected()
    {
        // Strict parsing on AiManifest rejects unknown sub-keys like the
        // pre-ADR-0043 ai.prompt: slot. The YAML library raises a parse
        // error; ManifestParseException wraps it.
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: my-unit
            description: x
            ai:
              runtime: claude-code
              prompt: |
                You are a unit.
            """;

        Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));
    }

    // ── Name uniqueness across depths ────────────────────────────────────

    [Fact]
    public void ParseAndResolve_DuplicateUnitNameAcrossDepths_Rejected()
    {
        // Two units named `worker` — one at the package root, one nested.
        // Name uniqueness within a package is required regardless of folder
        // depth (ADR-0043 §3).
        using var pkg = BuildPackage(rootYaml: PackageHeaderYaml,
            artefacts: new[]
            {
                ("units/worker", "Unit", "worker"),
                ("units/eng/units/worker", "Unit", "worker"),
                ("units/eng", "Unit", "eng"),
            });

        var ex = Should.Throw<PackageParseException>(() =>
            PackageManifestParser.ParseAndResolveAsync(
                File.ReadAllText(Path.Combine(pkg.Root, "package.yaml")),
                pkg.Root).GetAwaiter().GetResult());
        ex.Message.ShouldContain("Duplicate");
        ex.Message.ShouldContain("worker");
    }

    // ── Cycle detection — members: cycle ─────────────────────────────────

    [Fact]
    public async Task ParseAndResolve_MembersCycle_RejectedAsOutOfScope()
    {
        // Two top-level peer units pointing at each other via `members:`
        // is rejected by the ADR-0043 §3 scope check before the cycle
        // detector sees the graph — a top-level unit isn't owned by the
        // declaring unit, so `unit: a` from `units/b/` (and vice versa)
        // is `UnitMemberOutOfScope`. The new scope rule prevents this
        // class of cycle from forming in the first place.
        using var pkg = BuildPackageRaw(rootYaml: PackageHeaderYaml,
            entries: new[]
            {
                ("units/a/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: a
                    description: x
                    members:
                      - unit: b
                    """),
                ("units/b/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: b
                    description: x
                    members:
                      - unit: a
                    """),
            });

        var ex = await Should.ThrowAsync<PackageParseException>(async () =>
            await PackageManifestParser.ParseAndResolveAsync(
                File.ReadAllText(Path.Combine(pkg.Root, "package.yaml")),
                pkg.Root,
                cancellationToken: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("UnitMemberOutOfScope");
    }

    // ── Cycle detection — from: cycle ────────────────────────────────────

    [Fact]
    public async Task ParseAndResolve_FromCycle_Rejected()
    {
        // Two unit templates extending each other via `from:` — must be
        // detected as a cycle per ADR-0043 §7.
        using var pkg = BuildPackageRaw(rootYaml: PackageHeaderYaml,
            entries: new[]
            {
                ("templates/a/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: UnitTemplate
                    name: a
                    description: x
                    from: b
                    """),
                ("templates/b/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: UnitTemplate
                    name: b
                    description: x
                    from: a
                    """),
            });

        await Should.ThrowAsync<PackageCycleException>(async () =>
            await PackageManifestParser.ParseAndResolveAsync(
                File.ReadAllText(Path.Combine(pkg.Root, "package.yaml")),
                pkg.Root,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Happy-path resolution counts ─────────────────────────────────────

    [Fact]
    public async Task ParseAndResolve_NewShape_ReturnsExpectedCounts()
    {
        using var pkg = BuildPackage(rootYaml: PackageHeaderYaml,
            artefacts: new[]
            {
                ("units/root-unit", "Unit", "root-unit"),
                ("units/sub-unit", "Unit", "sub-unit"),
                ("agents/worker", "Agent", "worker"),
            });

        var resolved = await PackageManifestParser.ParseAndResolveAsync(
            File.ReadAllText(Path.Combine(pkg.Root, "package.yaml")),
            pkg.Root,
            cancellationToken: TestContext.Current.CancellationToken);

        resolved.Name.ShouldBe("my-package");
        resolved.Units.Count.ShouldBe(2);
        resolved.Agents.Count.ShouldBe(1);
    }

    // ── Test helpers ─────────────────────────────────────────────────────

    private const string PackageHeaderYaml = """
        apiVersion: spring.voyage/v1
        kind: Package
        name: my-package
        description: A walker-test package.
        version: 1.0.0
        """;

    /// <summary>
    /// Lays down a package tree with one inner artefact per entry. Each
    /// <paramref name="artefacts"/> tuple is
    /// <c>(relative-folder, kind, name)</c> — the inner package.yaml is
    /// generated to match the kind / name.
    /// </summary>
    private static TempPackage BuildPackage(
        string rootYaml,
        (string Folder, string Kind, string Name)[] artefacts)
    {
        var entries = artefacts.Select(a =>
        {
            var manifest = $"apiVersion: spring.voyage/v1\nkind: {a.Kind}\nname: {a.Name}\ndescription: x\n";
            return (a.Folder + "/package.yaml", manifest);
        }).ToArray();
        return BuildPackageRaw(rootYaml, entries);
    }

    /// <summary>
    /// Lays down a package tree with each <paramref name="entries"/> tuple
    /// as <c>(relative-file-path, raw-content)</c>. The caller controls
    /// the manifest contents verbatim.
    /// </summary>
    private static TempPackage BuildPackageRaw(
        string rootYaml,
        (string RelativePath, string Content)[] entries)
    {
        var root = Path.Combine(Path.GetTempPath(), "sv-walker-" + Path.GetRandomFileName());
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
