// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System.IO;
using System.Threading.Tasks;

using Cvoya.Spring.Manifest;

using Shouldly;

using Xunit;

/// <summary>
/// Regression test for the <c>packages/hello-world/</c> catalog package
/// (#2115). The package's whole reason to exist is that <c>spring package
/// install hello-world</c> succeeds without a <c>--connector</c> flag —
/// every other in-tree package declares <c>requires: [{ connector:
/// github }]</c> on at least one member, which forces the install
/// pipeline to demand a connector binding before it will accept the
/// install. The fast-pool E2E surface needs a connector-free path; this
/// test guards that contract against accidental regressions.
/// </summary>
public class HelloWorldPackageTests
{
    [Fact(Skip = "Pending Chunk 4 in-repo packages/hello-world migration to ADR-0043 recursive layout (issue #2304).")]
    public async Task HelloWorld_ResolvedManifest_HasNoConnectorRequirements()
    {
        var packageRoot = LocatePackageRoot();
        var yaml = await File.ReadAllTextAsync(
            Path.Combine(packageRoot, "package.yaml"),
            TestContext.Current.CancellationToken);

        var resolved = await PackageManifestParser.ParseAndResolveAsync(
            yaml, packageRoot,
            cancellationToken: TestContext.Current.CancellationToken);

        // The whole point: no connector slug surfaces from the union of
        // every contained artefact's `requires:` block. If a future edit
        // adds `requires: [{ connector: <slug> }]` to the unit or the
        // greeter agent, this test fails — and the package stops being
        // fast-pool-installable without a stub binding.
        resolved.RequiredConnectorSlugs.ShouldBeEmpty(
            "hello-world ships connector-free; adding a connector requirement defeats #2115");
    }

    [Fact(Skip = "Pending Chunk 4 in-repo packages/hello-world migration to ADR-0043 recursive layout (issue #2304).")]
    public async Task HelloWorld_ResolvedManifest_ShipsExactlyOneUnit()
    {
        // Smoke-test that the install pipeline actually has something to
        // do. A package with no artefacts would technically also have an
        // empty connector-requirement set — this guard keeps the package
        // exercising the end-to-end install path rather than degenerating
        // into a no-op. Agent members are activated by the per-unit
        // creator via the directory fall-back (see ResolveReferencesWith
        // DescendantsAsync), so they don't surface in
        // <c>resolved.Agents</c>; we instead assert the agent YAML exists
        // on disk and the unit references it.
        var packageRoot = LocatePackageRoot();
        var yaml = await File.ReadAllTextAsync(
            Path.Combine(packageRoot, "package.yaml"),
            TestContext.Current.CancellationToken);

        var resolved = await PackageManifestParser.ParseAndResolveAsync(
            yaml, packageRoot,
            cancellationToken: TestContext.Current.CancellationToken);

        resolved.Units.Count.ShouldBe(1, "hello-world ships exactly one unit");
        resolved.Units[0].Name.ShouldBe("hello-world");

        File.Exists(Path.Combine(packageRoot, "agents", "greeter.yaml"))
            .ShouldBeTrue("hello-world ships a greeter agent on disk");

        // The unit's resolved YAML body must reference the greeter so the
        // activator's directory fall-back can wire the membership.
        var unitContent = resolved.Units[0].Content;
        unitContent.ShouldNotBeNull();
        unitContent.ShouldContain("agent: greeter");
    }

    private static string LocatePackageRoot()
    {
        // Mirror the LocatePackageRoot pattern used by
        // SpringVoyageOssMigrationTests: walk up from the test binary
        // location to find the repo root, then the hello-world package
        // directory.
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "packages", "hello-world");
            if (Directory.Exists(candidate)
                && File.Exists(Path.Combine(candidate, "package.yaml")))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate packages/hello-world/ from the test binary path. " +
            "The test relies on the on-disk hello-world package layout to verify " +
            "the package stays connector-free for #2115.");
    }
}
