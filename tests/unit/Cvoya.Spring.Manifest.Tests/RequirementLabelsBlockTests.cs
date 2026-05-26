// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Parser + emitter tests for the optional <c>labels:</c> sibling on a
/// <c>connector:</c> requirement entry (issue #2780). The sibling
/// pre-seeds the install wizard's binding form so installers don't have
/// to re-type filters the package author already declared.
/// </summary>
public class RequirementLabelsBlockTests
{
    // ── Parsing ─────────────────────────────────────────────────────────

    [Fact]
    public void RequirementEntry_BareConnector_ParsesAsBefore()
    {
        // Pre-amendment shape must keep working; the amendment is purely
        // additive (the strict single-key form is still valid).
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: my-unit
            description: x
            requires:
              - connector: github
            """;

        var manifest = ManifestParser.Parse(yaml);

        manifest.Requires!.Count.ShouldBe(1);
        var entry = manifest.Requires[0];
        entry.Type.ShouldBe(RequirementType.Connector);
        entry.Identifier.ShouldBe("github");
        entry.Labels.ShouldBeNull();
    }

    [Fact]
    public void RequirementEntry_LabelsSibling_IncludeOnly_Parses()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: my-unit
            description: x
            requires:
              - connector: github
                labels:
                  include:
                    - spring-voyage-team
                    - area:*
            """;

        var manifest = ManifestParser.Parse(yaml);

        var entry = manifest.Requires!.Single();
        entry.Identifier.ShouldBe("github");
        entry.Labels.ShouldNotBeNull();
        entry.Labels!.Include.ShouldBe(new[] { "spring-voyage-team", "area:*" });
        entry.Labels.Exclude.ShouldBeEmpty();
    }

    [Fact]
    public void RequirementEntry_LabelsSibling_IncludeAndExclude_Parses()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: my-unit
            description: x
            requires:
              - connector: github
                labels:
                  include: [spring-voyage-team]
                  exclude: [wip, internal:*]
            """;

        var manifest = ManifestParser.Parse(yaml);

        var labels = manifest.Requires!.Single().Labels!;
        labels.Include.ShouldBe(new[] { "spring-voyage-team" });
        labels.Exclude.ShouldBe(new[] { "wip", "internal:*" });
    }

    [Fact]
    public void RequirementEntry_LabelsSibling_TrimsWhitespaceAndDropsEmpty()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: my-unit
            description: x
            requires:
              - connector: github
                labels:
                  include:
                    - "  spring-voyage-team  "
                    - ""
                    - "area:*"
            """;

        var manifest = ManifestParser.Parse(yaml);

        manifest.Requires!.Single().Labels!.Include
            .ShouldBe(new[] { "spring-voyage-team", "area:*" });
    }

    [Fact]
    public void RequirementEntry_UnknownSibling_Rejected()
    {
        // The amendment only loosens the strict single-key check for
        // known siblings; unknown siblings still raise so package authors
        // get a clean error rather than a silent drop.
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: my-unit
            description: x
            requires:
              - connector: github
                events: [issues]
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));
        ex.Message.ShouldContain("events");
    }

    [Fact]
    public void RequirementEntry_LabelsWithUnknownSubKey_Rejected()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: my-unit
            description: x
            requires:
              - connector: github
                labels:
                  authors: [octocat]
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));
        ex.Message.ShouldContain("authors");
    }

    [Fact]
    public void RequirementEntry_LabelsDeclaredTwice_Rejected()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: my-unit
            description: x
            requires:
              - connector: github
                labels:
                  include: [a]
                labels:
                  include: [b]
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));
        ex.Message.ShouldContain("labels");
    }

    // ── Emit / round-trip ───────────────────────────────────────────────

    [Fact]
    public void RequirementEntry_RoundTripsWithLabels()
    {
        var entry = new RequirementEntry
        {
            Type = RequirementType.Connector,
            Identifier = "github",
            Labels = new RequirementLabelsBlock
            {
                Include = new[] { "spring-voyage-team", "area:*" },
                Exclude = new[] { "wip" },
            },
        };

        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new RequirementEntryYamlConverter())
            .Build();
        var yaml = serializer.Serialize(new[] { entry });

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new RequirementEntryYamlConverter())
            .Build();
        var roundTripped = deserializer.Deserialize<RequirementEntry[]>(yaml).Single();

        roundTripped.Type.ShouldBe(RequirementType.Connector);
        roundTripped.Identifier.ShouldBe("github");
        roundTripped.Labels.ShouldNotBeNull();
        roundTripped.Labels!.Include.ShouldBe(new[] { "spring-voyage-team", "area:*" });
        roundTripped.Labels.Exclude.ShouldBe(new[] { "wip" });
    }

    [Fact]
    public void RequirementEntry_EmptyLabels_OmittedFromEmit()
    {
        // An entry whose Labels is non-null but empty (e.g. constructed by
        // code that didn't realise the block was a no-op) must not emit
        // a `labels:` key — the wire shape stays clean and round-trips
        // back to Labels == null.
        var entry = new RequirementEntry
        {
            Type = RequirementType.Connector,
            Identifier = "github",
            Labels = new RequirementLabelsBlock(),
        };

        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new RequirementEntryYamlConverter())
            .Build();
        var yaml = serializer.Serialize(new[] { entry });

        yaml.ShouldNotContain("labels");
    }

    // ── Cross-artefact fold + conflict detection ────────────────────────

    [Fact]
    public async Task ParseAndResolve_LabelsFoldFromDeclaringArtefact()
    {
        using var pkg = await CreatePackageAsync(
            packageYaml: """
                apiVersion: spring.voyage/v1
                kind: Package
                name: pkg
                description: x
                version: 1.0.0
                """,
            unitFiles: new[]
            {
                ("alpha", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    requires:
                      - connector: github
                        labels:
                          include: [spring-voyage-team]
                    """),
            });

        var resolved = await PackageManifestParser.ParseAndResolveAsync(
            pkg.PackageYaml, pkg.PackageRoot,
            cancellationToken: TestContext.Current.CancellationToken);

        resolved.ConnectorLabelsBySlug.ShouldContainKey("github");
        var labels = resolved.ConnectorLabelsBySlug["github"];
        labels.Include.ShouldBe(new[] { "spring-voyage-team" });
        labels.Exclude.ShouldBeEmpty();
    }

    [Fact]
    public async Task ParseAndResolve_LabelsFold_MatchingArtefacts_Succeed()
    {
        // Two artefacts may declare the same `(type, identifier)` requirement;
        // matching `labels:` blocks fold to a single value without complaint.
        using var pkg = await CreatePackageAsync(
            packageYaml: """
                apiVersion: spring.voyage/v1
                kind: Package
                name: pkg
                description: x
                version: 1.0.0
                """,
            unitFiles: new[]
            {
                ("alpha", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    requires:
                      - connector: github
                        labels:
                          include: [team]
                    """),
                ("beta", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: beta
                    description: x
                    requires:
                      - connector: github
                        labels:
                          include: [team]
                    """),
            });

        var resolved = await PackageManifestParser.ParseAndResolveAsync(
            pkg.PackageYaml, pkg.PackageRoot,
            cancellationToken: TestContext.Current.CancellationToken);

        resolved.ConnectorLabelsBySlug["github"].Include.ShouldBe(new[] { "team" });
    }

    [Fact]
    public async Task ParseAndResolve_LabelsFold_ConflictingArtefacts_Rejected()
    {
        // The fold raises a structured PackageParseException when two
        // artefacts disagree, naming both so the author can consolidate.
        using var pkg = await CreatePackageAsync(
            packageYaml: """
                apiVersion: spring.voyage/v1
                kind: Package
                name: pkg
                description: x
                version: 1.0.0
                """,
            unitFiles: new[]
            {
                ("alpha", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    requires:
                      - connector: github
                        labels:
                          include: [team-a]
                    """),
                ("beta", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: beta
                    description: x
                    requires:
                      - connector: github
                        labels:
                          include: [team-b]
                    """),
            });

        var ex = await Should.ThrowAsync<PackageParseException>(async () =>
            await PackageManifestParser.ParseAndResolveAsync(
                pkg.PackageYaml, pkg.PackageRoot,
                cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("alpha");
        ex.Message.ShouldContain("beta");
        ex.Message.ShouldContain("github");
    }

    [Fact]
    public async Task ParseAndResolve_LabelsFold_BareAndDecorated_NoConflict()
    {
        // One artefact declares the connector requirement bare; another
        // declares it with `labels:`. That's fine — the bare entry just
        // doesn't contribute defaults; the labels-bearing entry seeds them.
        using var pkg = await CreatePackageAsync(
            packageYaml: """
                apiVersion: spring.voyage/v1
                kind: Package
                name: pkg
                description: x
                version: 1.0.0
                """,
            unitFiles: new[]
            {
                ("alpha", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    requires:
                      - connector: github
                    """),
                ("beta", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: beta
                    description: x
                    requires:
                      - connector: github
                        labels:
                          include: [team]
                    """),
            });

        var resolved = await PackageManifestParser.ParseAndResolveAsync(
            pkg.PackageYaml, pkg.PackageRoot,
            cancellationToken: TestContext.Current.CancellationToken);

        resolved.ConnectorLabelsBySlug["github"].Include.ShouldBe(new[] { "team" });
    }

    // ── Helper: minimal on-disk package fixture ─────────────────────────

    private static async Task<TempPackage> CreatePackageAsync(
        string packageYaml,
        (string Name, string Content)[] unitFiles)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "sv-2780-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        var unitsDir = Path.Combine(tempRoot, "units");
        Directory.CreateDirectory(unitsDir);

        var packagePath = Path.Combine(tempRoot, "package.yaml");
        await File.WriteAllTextAsync(packagePath, packageYaml);

        foreach (var (name, content) in unitFiles)
        {
            var unitDir = Path.Combine(unitsDir, name);
            Directory.CreateDirectory(unitDir);
            await File.WriteAllTextAsync(Path.Combine(unitDir, "package.yaml"), content);
        }

        return new TempPackage(tempRoot, packageYaml);
    }

    private sealed class TempPackage : System.IDisposable
    {
        public string PackageRoot { get; }
        public string PackageYaml { get; }

        public TempPackage(string packageRoot, string packageYaml)
        {
            PackageRoot = packageRoot;
            PackageYaml = packageYaml;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(PackageRoot))
                {
                    Directory.Delete(PackageRoot, recursive: true);
                }
            }
            catch { /* best effort */ }
        }
    }
}
