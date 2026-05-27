// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Cvoya.Spring.Manifest;

using Shouldly;

using Xunit;

/// <summary>
/// Smoke tests for the ADR-0043 example packages
/// (<c>packages/example-simple/</c> and <c>packages/example-templated/</c>)
/// shipped alongside the in-repo migration in chunk 4. These packages
/// exist to demonstrate the recursive folder shape — instance-only and
/// template-based — and so the on-disk authoring must continue to walk
/// cleanly through the catalog walker + template resolver.
/// </summary>
public class ExamplePackageTests
{
    [Fact]
    public async Task ExampleSimple_Walks_AsInstanceOnlyTree()
    {
        var packageRoot = LocatePackageRoot("example-simple");
        var yaml = await File.ReadAllTextAsync(
            Path.Combine(packageRoot, "package.yaml"),
            TestContext.Current.CancellationToken);

        var resolved = await PackageManifestParser.ParseAndResolveAsync(
            yaml, packageRoot,
            cancellationToken: TestContext.Current.CancellationToken);

        // One top-level unit (greeting-team) plus two nested concrete
        // agents (friendly-greeter, polite-greeter) — both surface in the
        // resolved Agents list since the walker indexes every artefact in
        // the package regardless of depth (ADR-0043 §3).
        resolved.Units.ShouldContain(u => u.Name == "greeting-team");
        resolved.Agents.ShouldContain(a => a.Name == "friendly-greeter");
        resolved.Agents.ShouldContain(a => a.Name == "polite-greeter");

        // The two agent-scoped skills also surface — folder under the
        // owning agent (ADR-0043 §3 "Artefacts can ship their own nested
        // children").
        resolved.Skills.ShouldContain(s => s.Name == "say-hello");
        resolved.Skills.ShouldContain(s => s.Name == "say-hello-formally");

        // example-simple ships connector-free.
        resolved.RequiredConnectorSlugs.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExampleTemplated_Walks_AndTemplateResolverStampsChildren()
    {
        var packageRoot = LocatePackageRoot("example-templated");
        var yaml = await File.ReadAllTextAsync(
            Path.Combine(packageRoot, "package.yaml"),
            TestContext.Current.CancellationToken);

        var resolved = await PackageManifestParser.ParseAndResolveAsync(
            yaml, packageRoot,
            cancellationToken: TestContext.Current.CancellationToken);

        // The walker surfaces the top-level concrete unit (platform-eng),
        // the three concrete software-engineer instances (ada, hopper,
        // lovelace), and the template-side concrete children (team-lead,
        // senior-engineer at templates/engineering-team/agents/...).
        // After template-resolver stamping, the templates themselves drop
        // out of the activated set; the platform-eng instance carries
        // stamped clones of the template's children.
        resolved.Units.ShouldContain(u => u.Name == "platform-eng");

        // Run the template resolver to verify the stamped tree.
        var resolverOutput = await new TemplateResolver().ResolveAsync(
            resolved, packageRoot, TestContext.Current.CancellationToken);

        // platform-eng survives — it's the concrete instance.
        resolverOutput.Units.Single(u => u.Name == "platform-eng")
            .ShouldNotBeNull("platform-eng is the concrete activated unit");

        // The three software-engineer instances survive and were stamped
        // through their `from:` chain (their resolved Content carries the
        // merged template body).
        var ada = resolverOutput.Agents.Single(a => a.Name == "ada");
        ada.Content.ShouldNotBeNull("ada must carry the stamped template body");
        ada.Content!.ShouldContain("software-engineer",
            customMessage: "ada is stamped from the software-engineer template");

        resolverOutput.Agents.ShouldContain(a => a.Name == "hopper");
        resolverOutput.Agents.ShouldContain(a => a.Name == "lovelace");

        // example-templated ships connector-free.
        resolved.RequiredConnectorSlugs.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("hello-world")]
    [InlineData("research")]
    [InlineData("product-management")]
    [InlineData("software-engineering")]
    [InlineData("spring-voyage-oss")]
    [InlineData("example-simple")]
    [InlineData("example-templated")]
    [InlineData("magazine")]
    [InlineData("conversational-defaults")]
    [InlineData("engineer-defaults")]
    public async Task EveryInRepoPackage_ParsesUnderRecursiveLayout(string packageName)
    {
        // Smoke: every in-repo package must walk cleanly under the
        // ADR-0043 recursive folder layout. A regression that introduces
        // a stray flat-shape file, a legacy `content:` block, or a
        // folder-name / `name:` mismatch trips this check before it
        // reaches a portal install attempt.
        var packageRoot = LocatePackageRoot(packageName);
        var yaml = await File.ReadAllTextAsync(
            Path.Combine(packageRoot, "package.yaml"),
            TestContext.Current.CancellationToken);

        var resolved = await PackageManifestParser.ParseAndResolveAsync(
            yaml, packageRoot,
            cancellationToken: TestContext.Current.CancellationToken);

        resolved.ShouldNotBeNull();
        resolved.Name.ShouldBe(packageName);
    }

    private static string LocatePackageRoot(string packageName)
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "packages", packageName);
            if (Directory.Exists(candidate)
                && File.Exists(Path.Combine(candidate, "package.yaml")))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate packages/{packageName}/ from the test binary path. " +
            "The test relies on the on-disk example package layout.");
    }
}
