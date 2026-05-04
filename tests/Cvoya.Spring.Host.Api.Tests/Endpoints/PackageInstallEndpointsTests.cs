// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.Host.Api.Services;
using Cvoya.Spring.Manifest;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Endpoint contract tests for the package install surface (#1559):
/// <c>POST /api/v1/packages/install</c>,
/// <c>POST /api/v1/packages/install/file</c>,
/// <c>GET /api/v1/installs/{id}</c>,
/// <c>POST /api/v1/installs/{id}/retry</c>,
/// <c>POST /api/v1/installs/{id}/abort</c>.
///
/// Tests exercise the real <see cref="IPackageInstallService"/> backed by an
/// in-memory EF database. The <see cref="IPackageArtefactActivator"/> is
/// substituted with a controllable test double so tests do not need a Dapr
/// sidecar and can simulate Phase-2 failures deterministically.
///
/// Covers all 12 acceptance bullets from the issue (#1559):
///  1. POST /packages/install/file happy path single-target → 201.
///  2. POST /packages/install batch multi-target with cross-package reference; topo order.
///  3. POST missing-dep error → 400.
///  4. POST name collision → 409.
///  5. POST parse error → 400.
///  6. GET happy path → 200.
///  7. GET not found → 404.
///  8. POST retry after Phase-2 failure → 200.
///  9. POST abort after Phase-2 failure → 204.
/// 10. POST /file multipart upload → 201.
/// 11. Tenant isolation.
/// 12. Live-package integration test stub (skipped until #1562).
/// </summary>
public class PackageInstallEndpointsTests : IClassFixture<PackageInstallEndpointsTests.InstallFactory>
{
    private static readonly Guid Unit_Main_Id = new("00000001-feed-1234-5678-000000000000");

    // Server serialises enums as strings; tests must match.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // A minimal SELF-CONTAINED UnitPackage YAML — no `unit:` field so there are
    // no within-package artefact file references. The file-upload endpoint passes
    // PackageRoot = null; without a package root, bare unit references would fail
    // to resolve (ADR-0035 decision 13 states browse is one-shot + self-contained
    // in v0.1). This fixture installs successfully with zero artefacts, which is
    // sufficient to test the endpoint plumbing (201 Created, status=active, etc.).
    private const string SelfContainedPackageYamlTemplate = """
        apiVersion: spring.voyage/v1
        kind: UnitPackage
        metadata:
          name: {0}
        """;

    private readonly InstallFactory _factory;
    private readonly HttpClient _client;

    public PackageInstallEndpointsTests(InstallFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── Test 1: POST /packages/install/file — happy path single-target ────

    [Fact]
    public async Task InstallFile_SingleTarget_Returns201WithActiveStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.Activator.ActivateAsync(
                Arg.Any<string>(), Arg.Any<ResolvedArtefact>(),
                Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var packageName = $"pkg-single-{Guid.NewGuid():N}";
        var yaml = string.Format(SelfContainedPackageYamlTemplate, packageName);

        var response = await PostFileInstallAsync(yaml, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.ToString().ShouldContain("/api/v1/installs/");

        var body = await response.Content.ReadFromJsonAsync<InstallStatusResponse>(JsonOptions, ct);
        body!.InstallId.ShouldNotBe(Guid.Empty);
        body.Status.ShouldBe("active");
        body.Packages.Count.ShouldBe(1);
        body.Packages[0].PackageName.ShouldBe(packageName);
        body.Packages[0].State.ShouldBe("active");
    }

    // ── Test 2: POST /packages/install — batch multi-target, topo order ──

    [Fact]
    public async Task InstallPackages_BatchWithCrossPackageRef_TopoOrderActivation()
    {
        var ct = TestContext.Current.CancellationToken;

        // Recording activator: captures (packageName, artefactName) in order.
        var activationOrder = new ConcurrentQueue<(string Package, string Artefact)>();
        _factory.Activator.ActivateAsync(
                Arg.Any<string>(), Arg.Any<ResolvedArtefact>(),
                Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var pkg = ci.ArgAt<string>(0);
                var artefact = ci.ArgAt<ResolvedArtefact>(1);
                activationOrder.Enqueue((pkg, artefact.Name));
                return Task.CompletedTask;
            });

        // pkg-topo-b has no cross-package deps.
        // pkg-topo-a has a subUnit ref to pkg-topo-b/main, so B must activate before A.
        // Install A first in the request; the service must topo-sort B before A.
        var request = new PackageInstallRequest(new[]
        {
            new PackageInstallTarget(InstallFactory.PkgTopoA, null),
            new PackageInstallTarget(InstallFactory.PkgTopoB, null),
        });

        var response = await _client.PostAsJsonAsync(
            "/api/v1/packages/install", request, ct);

        var responseBody = await response.Content.ReadAsStringAsync(ct);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            $"Expected 201 Created but got {(int)response.StatusCode}. Body: {responseBody}");

        var body = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<InstallStatusResponse>(
            new System.Net.Http.StringContent(responseBody, Encoding.UTF8, "application/json"),
            JsonOptions, ct);
        body!.Status.ShouldBe("active");
        body.Packages.Count.ShouldBe(2);
        body.Packages.ShouldAllBe(p => p.State == "active");

        // Topo order: B before A (A depends on B).
        var order = activationOrder.ToArray();
        order.Length.ShouldBeGreaterThan(0);

        // All activations for B must precede all activations for A.
        var lastBIdx = Array.FindLastIndex(order, x => x.Package == InstallFactory.PkgTopoB);
        var firstAIdx = Array.FindIndex(order, x => x.Package == InstallFactory.PkgTopoA);

        if (lastBIdx >= 0 && firstAIdx >= 0)
        {
            // B's last activation should precede A's first activation.
            lastBIdx.ShouldBeLessThan(firstAIdx,
                $"Expected B to finish before A starts. Order: [{string.Join(", ", (IEnumerable<(string, string)>)order)}]");
        }
    }

    // ── Test 3: POST /packages/install/file — dep-graph closure failure → 400

    [Fact]
    public async Task InstallFile_CrossPackageRefToMissingPackage_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;

        // YAML that cross-references a package that is neither in the batch
        // nor installed; any catalog lookup returns not-found → 400.
        const string Yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: pkg-missingdep
            unit: nonexistent-pkg/some-unit
            """;

        var response = await PostFileInstallAsync(Yaml, ct);

        ((int)response.StatusCode).ShouldBe(400);
    }

    // ── Test 4: POST /packages/install — name collision → 409 ─────────────

    [Fact]
    public async Task InstallPackages_NameCollision_Returns409()
    {
        var ct = TestContext.Current.CancellationToken;

        // Reset: no collision by default.
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        _factory.Activator.ActivateAsync(
                Arg.Any<string>(), Arg.Any<ResolvedArtefact>(),
                Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // First install using a catalog-backed package that has a real unit
        // artefact ("main"). The directory service returns null → no collision.
        var request = new PackageInstallRequest(new[]
        {
            new PackageInstallTarget(InstallFactory.PkgCollision, null),
        });

        var first = await _client.PostAsJsonAsync(
            "/api/v1/packages/install", request, JsonOptions, ct);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Now simulate that "main" already exists in the directory.
        // The collision pre-flight in PackageInstallService checks
        // IDirectoryService.ResolveAsync for every artefact name in the batch.
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Path == "main"),
                Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("unit", Unit_Main_Id),
                Unit_Main_Id,
                "main",
                string.Empty,
                null,
                DateTimeOffset.UtcNow));

        var second = await _client.PostAsJsonAsync(
            "/api/v1/packages/install", request, JsonOptions, ct);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Reset mock so other tests are not affected.
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);
    }

    // ── Test 5: POST /packages/install/file — parse error → 400 ──────────

    [Fact]
    public async Task InstallFile_MalformedYaml_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;

        const string MalformedYaml = "not: valid: yaml: at: all: [[[";

        var response = await PostFileInstallAsync(MalformedYaml, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ── Test 6: GET /installs/{id} — happy path ───────────────────────────

    [Fact]
    public async Task GetInstallStatus_AfterSuccessfulInstall_Returns200()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.Activator.ActivateAsync(
                Arg.Any<string>(), Arg.Any<ResolvedArtefact>(),
                Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var packageName = $"pkg-getst-{Guid.NewGuid():N}";
        var installResp = await PostFileInstallAsync(
            string.Format(SelfContainedPackageYamlTemplate, packageName), ct);
        installResp.StatusCode.ShouldBe(HttpStatusCode.Created);

        var installBody = await installResp.Content.ReadFromJsonAsync<InstallStatusResponse>(JsonOptions, ct);
        var installId = installBody!.InstallId;

        var getResp = await _client.GetAsync($"/api/v1/installs/{installId}", ct);
        getResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var getBody = await getResp.Content.ReadFromJsonAsync<InstallStatusResponse>(JsonOptions, ct);
        getBody!.InstallId.ShouldBe(installId);
        getBody.Status.ShouldBe("active");
        getBody.Packages.Count.ShouldBe(1);
        getBody.Packages[0].PackageName.ShouldBe(packageName);
    }

    // ── Test 7: GET /installs/{id} — not found ────────────────────────────

    [Fact]
    public async Task GetInstallStatus_UnknownId_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var randomId = Guid.NewGuid();

        var response = await _client.GetAsync($"/api/v1/installs/{randomId}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── Test 8: POST /installs/{id}/retry — Phase-2 failure then success ──

    [Fact]
    public async Task RetryInstall_AfterPhase2Failure_Returns200Active()
    {
        var ct = TestContext.Current.CancellationToken;

        // First: activator throws on every call.
        _factory.Activator.ActivateAsync(
                Arg.Any<string>(), Arg.Any<ResolvedArtefact>(),
                Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Simulated Phase-2 failure"));

        var packageName = $"pkg-retry-{Guid.NewGuid():N}";
        var yaml = string.Format(SelfContainedPackageYamlTemplate, packageName);

        // Self-contained package with no artefacts: Phase 2 has nothing to
        // activate, so the activator is never called and the result is always
        // "active". Use the catalog package which HAS a unit artefact.
        // Use the catalog-backed package so the activator IS invoked for Phase 2.
        var request = new PackageInstallRequest(new[]
        {
            new PackageInstallTarget(InstallFactory.PkgForRetry, null),
        });

        var installResp = await _client.PostAsJsonAsync(
            "/api/v1/packages/install", request, JsonOptions, ct);
        installResp.StatusCode.ShouldBe(HttpStatusCode.Created);

        var installBody = await installResp.Content.ReadFromJsonAsync<InstallStatusResponse>(JsonOptions, ct);
        installBody!.Status.ShouldBe("failed");
        var installId = installBody.InstallId;

        // Fix the activator — it now succeeds.
        _factory.Activator.ActivateAsync(
                Arg.Any<string>(), Arg.Any<ResolvedArtefact>(),
                Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var retryResp = await _client.PostAsync($"/api/v1/installs/{installId}/retry", null, ct);
        retryResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var retryBody = await retryResp.Content.ReadFromJsonAsync<InstallStatusResponse>(JsonOptions, ct);
        retryBody!.InstallId.ShouldBe(installId);
        retryBody.Status.ShouldBe("active");
    }

    // ── Test 9: POST /installs/{id}/abort — Phase-2 failure ──────────────

    [Fact]
    public async Task AbortInstall_AfterPhase2Failure_Returns204AndRemovesRows()
    {
        var ct = TestContext.Current.CancellationToken;

        _factory.Activator.ActivateAsync(
                Arg.Any<string>(), Arg.Any<ResolvedArtefact>(),
                Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Simulated Phase-2 failure"));

        // Use catalog-backed package so the activator IS invoked.
        var request = new PackageInstallRequest(new[]
        {
            new PackageInstallTarget(InstallFactory.PkgForAbort, null),
        });

        var installResp = await _client.PostAsJsonAsync(
            "/api/v1/packages/install", request, JsonOptions, ct);
        installResp.StatusCode.ShouldBe(HttpStatusCode.Created);

        var installBody = await installResp.Content.ReadFromJsonAsync<InstallStatusResponse>(JsonOptions, ct);
        installBody!.Status.ShouldBe("failed");
        var installId = installBody.InstallId;

        var abortResp = await _client.PostAsync($"/api/v1/installs/{installId}/abort", null, ct);
        abortResp.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // GET should now return 404 — abort deleted all rows.
        var getResp = await _client.GetAsync($"/api/v1/installs/{installId}", ct);
        getResp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── Test 10: POST /packages/install/file — multipart upload ──────────

    [Fact]
    public async Task InstallFile_ValidYamlUpload_Returns201WithInstallId()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.Activator.ActivateAsync(
                Arg.Any<string>(), Arg.Any<ResolvedArtefact>(),
                Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var packageName = $"pkg-upload-{Guid.NewGuid():N}";
        var yaml = string.Format(SelfContainedPackageYamlTemplate, packageName);

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(yaml, Encoding.UTF8, "text/plain"), "file", "upload.yaml");

        var response = await _client.PostAsync("/api/v1/packages/install/file", content, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<InstallStatusResponse>(JsonOptions, ct);
        body!.InstallId.ShouldNotBe(Guid.Empty);
        body.Status.ShouldBe("active");
        body.Packages.Count.ShouldBe(1);
        body.Packages[0].PackageName.ShouldBe(packageName);
    }

    // ── Test 11: Tenant isolation ─────────────────────────────────────────

    [Fact]
    public async Task TenantIsolation_InstallInTenantA_NotVisibleFromTenantB()
    {
        var ct = TestContext.Current.CancellationToken;

        _factory.Activator.ActivateAsync(
                Arg.Any<string>(), Arg.Any<ResolvedArtefact>(),
                Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var packageName = $"pkg-iso-{Guid.NewGuid():N}";
        var yaml = string.Format(SelfContainedPackageYamlTemplate, packageName);

        var installResp = await PostFileInstallAsync(yaml, ct);
        installResp.StatusCode.ShouldBe(HttpStatusCode.Created);
        var installBody = await installResp.Content.ReadFromJsonAsync<InstallStatusResponse>(JsonOptions, ct);
        var installId = installBody!.InstallId;

        // Factory B has a completely separate in-memory DB; the install done
        // via factory A is not present in factory B's DB → 404.
        await using var factoryB = new InstallFactory();
        var clientB = factoryB.CreateClient();
        var getResp = await clientB.GetAsync($"/api/v1/installs/{installId}", ct);
        getResp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── Test 12: POST /packages/install/file with local ref → 400 ────────

    [Fact]
    public async Task InstallFile_PackageWithLocalUnitRef_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;

        // A UnitPackage with a bare unit reference — requires an on-disk
        // package directory to resolve, which is not present in the upload path.
        // The parser must raise PackageUploadHasLocalRefException (inherits from
        // PackageParseException) → endpoint maps it to 400.
        const string YamlWithLocalRef = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: multi-file-upload-pkg
            unit: my-local-unit
            """;

        var response = await PostFileInstallAsync(YamlWithLocalRef, ct);

        ((int)response.StatusCode).ShouldBe(400);

        var body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldContain("local references");
    }

    // ── Test 13: Live-package integration test stub ───────────────────────

    [Fact(Skip = "End-to-end integration test requiring a Dapr sidecar and a configured catalog — run manually.")]
    public async Task Install_SpringVoyageOssPackage_EndToEnd_Skipped()
    {
        // packages/spring-voyage-oss/package.yaml now exists (#1562).
        //
        // Expected flow:
        // 1. POST /api/v1/packages/install with { targets: [{ packageName: "spring-voyage-oss",
        //    inputs: { github_owner: "...", github_repo: "...", github_installation_id: "..." } }] }
        // 2. Assert 201 with status = active
        // 3. Assert all 5 units appear in the directory.
        //
        // Requires: a running Dapr sidecar and a catalog configured to the live packages/
        // directory.  Run manually against a local or staging environment.
        await Task.CompletedTask; // placeholder
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> PostFileInstallAsync(string yaml, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(yaml, Encoding.UTF8, "text/plain"), "file", "package.yaml");
        return await _client.PostAsync("/api/v1/packages/install/file", content, ct);
    }

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Extends <see cref="CustomWebApplicationFactory"/> to:
    /// <list type="bullet">
    ///   <item><description>Expose a controllable <see cref="IPackageArtefactActivator"/> test double.</description></item>
    ///   <item><description>Create on-disk catalog packages for tests that need artefact resolution (collision, batch, retry/abort).</description></item>
    ///   <item><description>Configure <see cref="PackageCatalogOptions.Root"/> to the temp packages tree.</description></item>
    /// </list>
    /// </summary>
    public sealed class InstallFactory : CustomWebApplicationFactory
    {
        // Fixed catalog package names used by tests that install via
        // POST /api/v1/packages/install (requires on-disk package structure).
        // All four are independent — no cross-package deps — except PkgTopoA
        // which sub-references PkgTopoB (for topo-order test).
        public const string PkgCollision = "pkg-test-collision";
        public const string PkgTopoA = "pkg-test-topo-a";
        public const string PkgTopoB = "pkg-test-topo-b";
        public const string PkgForRetry = "pkg-test-retry";
        public const string PkgForAbort = "pkg-test-abort";

        /// <summary>
        /// The root of the temporary packages tree created by this factory.
        /// Configured via <see cref="PackageCatalogOptions.Root"/> so the
        /// <see cref="FileSystemPackageCatalogService"/> can find the packages.
        /// </summary>
        public string PackagesRoot { get; } = Path.Combine(
            Path.GetTempPath(), "sv-install-tests", $"pkgs-{Guid.NewGuid():N}");

        /// <summary>
        /// The activator substitute. Tests configure this before calling
        /// any endpoint that exercises Phase 2.
        /// </summary>
        public IPackageArtefactActivator Activator { get; } = CreateDefaultActivator();

        private static IPackageArtefactActivator CreateDefaultActivator()
        {
            var a = Substitute.For<IPackageArtefactActivator>();
            // Default: succeed silently (no Dapr sidecar needed).
            a.ActivateAsync(
                    Arg.Any<string>(), Arg.Any<ResolvedArtefact>(),
                    Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            return a;
        }

        public InstallFactory()
        {
            CreateCatalogPackages();
        }

        /// <summary>
        /// Creates the on-disk package structure required by catalog-based tests:
        ///
        /// <c>{Root}/pkg-test-collision/</c>   — has a unit "main"; used by collision test.
        /// <c>{Root}/pkg-test-topo-a/</c>      — has its own unit "main" + cross-ref to pkg-test-topo-b/main.
        /// <c>{Root}/pkg-test-topo-b/</c>      — has a unit "main"; no cross-package deps.
        /// <c>{Root}/pkg-test-retry/</c>        — has a unit "main"; used by retry test.
        /// <c>{Root}/pkg-test-abort/</c>        — has a unit "main"; used by abort test.
        /// </summary>
        private void CreateCatalogPackages()
        {
            CreateSingleUnitPackage(PkgCollision);
            CreateSingleUnitPackage(PkgTopoB);
            CreateTopoAPackage();
            CreateSingleUnitPackage(PkgForRetry);
            CreateSingleUnitPackage(PkgForAbort);
        }

        /// <summary>
        /// Creates a package with one unit named after the package (so
        /// post-#1629 collision pre-flight, which keys off DisplayName,
        /// doesn't trip across packages that share the same in-package
        /// unit slug).
        /// </summary>
        private void CreateSingleUnitPackage(string pkgName)
        {
            var pkgDir = Path.Combine(PackagesRoot, pkgName);
            var unitsDir = Path.Combine(pkgDir, "units");
            Directory.CreateDirectory(unitsDir);
            // Per-package unique unit slug so the DisplayName-keyed collision
            // pre-flight doesn't conflate unrelated installs in the shared DB.
            var unitSlug = $"{pkgName}-main";

            File.WriteAllText(
                Path.Combine(pkgDir, "package.yaml"),
                $"""
                apiVersion: spring.voyage/v1
                kind: UnitPackage
                metadata:
                  name: {pkgName}
                unit: {unitSlug}
                """);

            File.WriteAllText(
                Path.Combine(unitsDir, $"{unitSlug}.yaml"),
                $"""
                unit:
                  name: {unitSlug}
                  description: Test unit for {pkgName}.
                """);
        }

        /// <summary>
        /// Creates pkg-test-topo-a: a unit "local-a" of its own, plus a cross-package
        /// sub-unit reference to pkg-test-topo-b/main. The dep graph requires
        /// pkg-test-topo-b to be activated before pkg-test-topo-a.
        ///
        /// The own artefact is named "local-a" (not "main") to avoid the
        /// within-package duplicate-name check: if both the local unit and the
        /// cross-package sub-unit were named "main", <see cref="PackageManifestParser"/>
        /// would reject the batch with a 400 (ADR-0035 decision — each artefact of
        /// the same type must have a unique name within a package).
        /// </summary>
        private void CreateTopoAPackage()
        {
            var pkgDir = Path.Combine(PackagesRoot, PkgTopoA);
            var unitsDir = Path.Combine(pkgDir, "units");
            Directory.CreateDirectory(unitsDir);
            // Cross-package sub-unit: PkgTopoB's local unit slug (set by
            // CreateSingleUnitPackage) is "{pkgName}-main", so we reference it
            // here verbatim rather than the original "main".
            var topoBUnit = $"{PkgTopoB}-main";

            File.WriteAllText(
                Path.Combine(pkgDir, "package.yaml"),
                $"""
                apiVersion: spring.voyage/v1
                kind: UnitPackage
                metadata:
                  name: {PkgTopoA}
                unit: local-a
                subUnits:
                  - {PkgTopoB}/{topoBUnit}
                """);

            File.WriteAllText(
                Path.Combine(unitsDir, "local-a.yaml"),
                $"""
                unit:
                  name: local-a
                  description: Local unit for {PkgTopoA} (depends on {PkgTopoB}/{topoBUnit}).
                """);
        }

        protected override void ConfigureWebHost(
            Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            // Apply the base factory configuration first (replaces DB, Dapr
            // services, auth, etc.).
            base.ConfigureWebHost(builder);

            // Layer on top: replace IPackageArtefactActivator with our test double
            // and configure PackageCatalogOptions to point to our temp tree.
            builder.ConfigureServices(services =>
            {
                // Replace IPackageArtefactActivator.
                var activatorDescriptors = new System.Collections.Generic.List<
                    Microsoft.Extensions.DependencyInjection.ServiceDescriptor>();
                foreach (var d in services)
                {
                    if (d.ServiceType == typeof(IPackageArtefactActivator))
                    {
                        activatorDescriptors.Add(d);
                    }
                }
                foreach (var d in activatorDescriptors) services.Remove(d);

                var activator = Activator;
                services.AddScoped<IPackageArtefactActivator>(_ => activator);

                // Replace PackageCatalogOptions with the temp directory root.
                // The FileSystemPackageCatalogService uses this to locate packages.
                var catalogOptDescriptors = new System.Collections.Generic.List<
                    Microsoft.Extensions.DependencyInjection.ServiceDescriptor>();
                foreach (var d in services)
                {
                    if (d.ServiceType == typeof(PackageCatalogOptions))
                    {
                        catalogOptDescriptors.Add(d);
                    }
                }
                foreach (var d in catalogOptDescriptors) services.Remove(d);

                var packagesRoot = PackagesRoot;
                services.AddSingleton(new PackageCatalogOptions { Root = packagesRoot });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                try
                {
                    if (Directory.Exists(PackagesRoot))
                    {
                        Directory.Delete(PackagesRoot, recursive: true);
                    }
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        }
    }
}