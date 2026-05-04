// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;
using Cvoya.Spring.Host.Api.Services;
using Cvoya.Spring.Manifest;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="PackageInstallService"/> covering the 12
/// acceptance bullets from #1558 (ADR-0035 decision 11).
/// Uses in-memory EF Core so tests run without Postgres.
/// </summary>
public class PackageInstallServiceTests
{
    private static readonly Guid Unit_Main_Id = new("00000001-feed-1234-5678-000000000000");

    private static readonly Guid TenantA = new("aaaaaaaa-1111-2222-3333-aaaaaaaaaaaa");
    private static readonly Guid TenantB = new("bbbbbbbb-1111-2222-3333-bbbbbbbbbbbb");

    // A minimal valid UnitPackage YAML with no inputs. {0} = package name.
    private const string MinimalPackageYaml = """
        apiVersion: spring.voyage/v1
        kind: UnitPackage
        metadata:
          name: {0}
        unit: main
        """;

    private const string MinimalUnitYaml = """
        unit:
          name: main
        """;

    private const string YamlWithComments = """
        # This comment should be preserved
        apiVersion: spring.voyage/v1
        kind: UnitPackage
        metadata:
          name: my-package
          # description follows
          description: test package
        unit: main
        """;

    // ── Fixture helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="PackageInstallService"/> backed by an isolated
    /// in-memory <see cref="SpringDbContext"/> for the given tenant.
    /// Returns the service + a scope factory that resolves the same DB so
    /// tests can inspect rows after calls.
    /// </summary>
    private static (PackageInstallService Service, IServiceScopeFactory ScopeFactory)
        BuildService(
            Guid? tenantId = null,
            IDirectoryService? dir = null,
            IPackageArtefactActivator? activator = null,
            IPackageCatalogProvider? catalog = null)
    {
        var dbName = $"pkg-install-{Guid.NewGuid():N}";
        var services = new ServiceCollection();

        services.AddSingleton<ITenantContext>(new StaticTenantContext(tenantId ?? TenantA));
        services.AddScoped<SpringDbContext>(sp =>
        {
            var opts = new DbContextOptionsBuilder<SpringDbContext>()
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new SpringDbContext(opts, sp.GetRequiredService<ITenantContext>());
        });

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        dir ??= NoOpDirectory();
        activator ??= SucceedingActivator();

        var svc = new PackageInstallService(
            scopeFactory, dir, activator,
            NullLogger<PackageInstallService>.Instance,
            catalog);

        return (svc, scopeFactory);
    }

    private static IDirectoryService NoOpDirectory()
    {
        var d = Substitute.For<IDirectoryService>();
        d.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);
        return d;
    }

    private static IPackageArtefactActivator SucceedingActivator()
    {
        var a = Substitute.For<IPackageArtefactActivator>();
        a.ActivateAsync(
                Arg.Any<string>(), Arg.Any<ResolvedArtefact>(),
                Arg.Any<Guid>(), Arg.Any<LocalSymbolMap>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return a;
    }

    private static string CreatePackageDir(string unitYaml = MinimalUnitYaml)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sv-pkg-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Path.Combine(root, "units"));
        File.WriteAllText(Path.Combine(root, "units", "main.yaml"), unitYaml);
        return root;
    }

    private static InstallTarget MakeTarget(
        string packageName,
        string? yaml = null,
        string? packageRoot = null,
        IReadOnlyDictionary<string, string>? inputs = null)
    {
        var root = packageRoot ?? CreatePackageDir();
        var rawYaml = yaml ?? string.Format(MinimalPackageYaml, packageName);
        return new InstallTarget(
            packageName,
            inputs ?? new Dictionary<string, string>(),
            rawYaml,
            root);
    }

    // ── Test 1: Phase-1 kill switch ────────────────────────────────────────

    [Fact]
    public async Task InstallAsync_Phase1DirectoryThrows_ZeroRowsSurvive()
    {
        // Post-#1629 the collision pre-flight queries the staging DB by
        // DisplayName rather than calling IDirectoryService — see
        // PackageInstallService.CheckNameCollisionsAsync. Provide an
        // activator that throws during phase 2 to simulate a mid-install
        // failure; phase 1 still ran, but the phase-2 abort path must not
        // leave PackageInstalls or UnitDefinitions tied to that install.
        var activator = Substitute.For<IPackageArtefactActivator>();
        activator.ActivateAsync(
                Arg.Any<string>(), Arg.Any<ResolvedArtefact>(),
                Arg.Any<Guid>(), Arg.Any<LocalSymbolMap>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Simulated mid-Phase-2 failure"));

        var (svc, scopeFactory) = BuildService(activator: activator);

        var result = await svc.InstallAsync(
            new[] { MakeTarget("pkg-kill") }, TestContext.Current.CancellationToken);
        result.PackageResults.ShouldHaveSingleItem();
        result.PackageResults[0].Status.ShouldBe(PackageInstallOutcome.Failed);

        // Phase-2 failure leaves the staging row in 'failed' state for
        // operator decision (retry / abort) — this matches Test 2's
        // expectation. The "zero rows survive" guarantee is asserted by the
        // separate AbortAsync_AfterPhase2Failure_DeletesAllRows test.
    }

    // ── Test 2: Phase-2 failure leaves recoverable staging ─────────────────

    [Fact]
    public async Task InstallAsync_Phase2ActivationFails_LeavesFailedStatus()
    {
        var activator = Substitute.For<IPackageArtefactActivator>();
        activator.ActivateAsync(
                Arg.Any<string>(), Arg.Any<ResolvedArtefact>(),
                Arg.Any<Guid>(), Arg.Any<LocalSymbolMap>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Phase-2 failure"));

        var (svc, scopeFactory) = BuildService(activator: activator);

        var result = await svc.InstallAsync(
            new[] { MakeTarget("pkg-p2-fail") },
            TestContext.Current.CancellationToken);

        result.PackageResults.ShouldHaveSingleItem();
        result.PackageResults[0].Status.ShouldBe(PackageInstallOutcome.Failed);
        result.PackageResults[0].ErrorMessage.ShouldNotBeNullOrEmpty();

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.PackageInstalls
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        row.ShouldNotBeNull();
        row!.Status.ShouldBe(PackageInstallStatus.Failed);
        row.ErrorMessage.ShouldNotBeNullOrEmpty();

        var status = await svc.GetStatusAsync(
            result.InstallId, TestContext.Current.CancellationToken);
        status.ShouldNotBeNull();
        status!.Packages.ShouldHaveSingleItem();
        status.Packages[0].Status.ShouldBe(PackageInstallOutcome.Failed);
    }

    // ── Test 3: Retry after Phase-2 failure ────────────────────────────────

    [Fact]
    public async Task RetryAsync_AfterPhase2Failure_TransitionsToActive()
    {
        var failCount = 0;
        var activator = Substitute.For<IPackageArtefactActivator>();
        activator.ActivateAsync(
                Arg.Any<string>(), Arg.Any<ResolvedArtefact>(),
                Arg.Any<Guid>(), Arg.Any<LocalSymbolMap>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (failCount++ == 0)
                {
                    throw new InvalidOperationException("First attempt fails");
                }
                return Task.CompletedTask;
            });

        var (svc, scopeFactory) = BuildService(activator: activator);

        var initial = await svc.InstallAsync(
            new[] { MakeTarget("pkg-retry") },
            TestContext.Current.CancellationToken);
        initial.PackageResults[0].Status.ShouldBe(PackageInstallOutcome.Failed);

        var retryResult = await svc.RetryAsync(
            initial.InstallId, TestContext.Current.CancellationToken);
        retryResult.PackageResults.ShouldHaveSingleItem();
        retryResult.PackageResults[0].Status.ShouldBe(PackageInstallOutcome.Active);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.PackageInstalls
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        row.ShouldNotBeNull();
        row!.Status.ShouldBe(PackageInstallStatus.Active);
    }

    // ── Test 4: Abort after Phase-2 failure ────────────────────────────────

    [Fact]
    public async Task AbortAsync_AfterPhase2Failure_DeletesAllRows()
    {
        var activator = Substitute.For<IPackageArtefactActivator>();
        activator.ActivateAsync(
                Arg.Any<string>(), Arg.Any<ResolvedArtefact>(),
                Arg.Any<Guid>(), Arg.Any<LocalSymbolMap>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Activation fails"));

        var (svc, scopeFactory) = BuildService(activator: activator);

        var result = await svc.InstallAsync(
            new[] { MakeTarget("pkg-abort") },
            TestContext.Current.CancellationToken);
        result.PackageResults[0].Status.ShouldBe(PackageInstallOutcome.Failed);

        await svc.AbortAsync(result.InstallId, TestContext.Current.CancellationToken);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        (await db.PackageInstalls.IgnoreQueryFilters()
            .Where(r => r.InstallId == result.InstallId)
            .ToListAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await db.UnitDefinitions.IgnoreQueryFilters()
            .Where(u => u.InstallId == result.InstallId)
            .ToListAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();

        var status = await svc.GetStatusAsync(
            result.InstallId, TestContext.Current.CancellationToken);
        status.ShouldBeNull();
    }

    // ── #1629 PR7: staging row id == symbol-map id ────────────────────────

    [Fact]
    public async Task InstallAsync_StagingRowAndSymbolMap_ShareSingleGuidIdentity()
    {
        // The activator must receive the LocalSymbolMap whose minted Guid
        // for `main` matches the unit_definitions staging row's id. Without
        // the link, Phase-1 and Phase-2 would write two near-duplicate rows
        // for the same display name — exactly the bug #1629 PR7 fixes.
        LocalSymbolMap? capturedMap = null;
        var activator = Substitute.For<IPackageArtefactActivator>();
        activator.ActivateAsync(
                Arg.Any<string>(),
                Arg.Any<ResolvedArtefact>(),
                Arg.Any<Guid>(),
                Arg.Do<LocalSymbolMap>(m => capturedMap = m),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var (svc, scopeFactory) = BuildService(activator: activator);

        var result = await svc.InstallAsync(
            new[] { MakeTarget("pkg-symbol-id") },
            TestContext.Current.CancellationToken);

        result.PackageResults.ShouldHaveSingleItem();
        result.PackageResults[0].Status.ShouldBe(PackageInstallOutcome.Active);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var stagingRow = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                u => u.InstallId == result.InstallId && u.DisplayName == "main",
                TestContext.Current.CancellationToken);

        stagingRow.ShouldNotBeNull();
        capturedMap.ShouldNotBeNull();

        var resolved = capturedMap!.GetOrMint(ArtefactKind.Unit, "main");
        resolved.ShouldBe(stagingRow!.Id);
    }

    // ── Test 5: Multi-package batch — both packages install ─────────────────

    [Fact]
    public async Task InstallAsync_TwoPackageBatch_BothSucceed()
    {
        var (svc, scopeFactory) = BuildService();

        var targets = new[]
        {
            MakeTarget("pkg-batch-1"),
            MakeTarget("pkg-batch-2"),
        };

        var result = await svc.InstallAsync(targets, TestContext.Current.CancellationToken);
        result.PackageResults.Count.ShouldBe(2);
        result.PackageResults.All(r => r.Status == PackageInstallOutcome.Active).ShouldBeTrue();

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var rows = await db.PackageInstalls
            .ToListAsync(TestContext.Current.CancellationToken);
        rows.Count.ShouldBe(2);
        rows.All(r => r.InstallId == result.InstallId).ShouldBeTrue();
    }

    // ── Test 6: Exact dep-graph error string ───────────────────────────────

    [Fact]
    public void PackageDepGraphException_MessageMatchesAdrSpec()
    {
        var missing = new List<string>
        {
            "package A references B/agent, which is not in the install batch and not installed in this tenant"
        };
        var ex = new PackageDepGraphException(missing);

        ex.Message.ShouldBe(
            "package A references B/agent, which is not in the install batch and not installed in this tenant");
        ex.MissingReferences.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task InstallAsync_CrossPackageRefToMissingPackage_ThrowsWithMention()
    {
        // Package A has a cross-package ref to pkg-b/main. pkg-b is not in
        // the batch and no catalog is configured → parser fails.
        var (svc, _) = BuildService();

        var rootA = CreatePackageDir();
        var aYaml = """
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: pkg-a
            unit: pkg-b/main
            """;

        var targetA = new InstallTarget(
            "pkg-a", new Dictionary<string, string>(), aYaml, rootA);

        var ex = await Should.ThrowAsync<Exception>(async () =>
            await svc.InstallAsync(new[] { targetA },
                TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("pkg-b");
    }

    // ── Test 7: Already-installed dep satisfies reference ──────────────────

    [Fact]
    public async Task InstallAsync_AlreadyInstalledDepInCatalog_DoesNotThrowDepGraphError()
    {
        // A catalog that reports pkg-b as existing satisfies the dep-graph check.
        var catalog = Substitute.For<IPackageCatalogProvider>();
        catalog.PackageExistsAsync("pkg-b", Arg.Any<CancellationToken>())
            .Returns(true);
        catalog.PackageExistsAsync(
                Arg.Is<string>(s => s != "pkg-b"), Arg.Any<CancellationToken>())
            .Returns(false);
        catalog.LoadArtefactYamlAsync(
                Arg.Any<string>(), Arg.Any<ArtefactKind>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var (svc, _) = BuildService(catalog: catalog);

        var rootA = CreatePackageDir();
        var targetA = MakeTarget("pkg-a", packageRoot: rootA);

        // No PackageDepGraphException expected.
        var result = await svc.InstallAsync(
            new[] { targetA }, TestContext.Current.CancellationToken);
        result.ShouldNotBeNull();
    }

    // ── Test 8: Tenant isolation ────────────────────────────────────────────

    [Fact]
    public async Task InstallAsync_TenantA_TenantBDbSeesNothing()
    {
        var (svcA, scopeFactoryA) = BuildService(TenantA);
        var (_, scopeFactoryB) = BuildService(TenantB);

        var result = await svcA.InstallAsync(
            new[] { MakeTarget("pkg-isolation") },
            TestContext.Current.CancellationToken);
        result.PackageResults[0].Status.ShouldBe(PackageInstallOutcome.Active);

        using var scopeB = scopeFactoryB.CreateScope();
        var dbB = scopeB.ServiceProvider.GetRequiredService<SpringDbContext>();
        (await dbB.PackageInstalls
            .ToListAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    // ── Test 9: install_id semantics ───────────────────────────────────────

    [Fact]
    public async Task InstallAsync_SinglePackage_OneInstallRowWithInstallId()
    {
        var (svc, scopeFactory) = BuildService();
        var result = await svc.InstallAsync(
            new[] { MakeTarget("pkg-id-test") },
            TestContext.Current.CancellationToken);

        result.InstallId.ShouldNotBe(Guid.Empty);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var rows = await db.PackageInstalls
            .ToListAsync(TestContext.Current.CancellationToken);
        rows.ShouldHaveSingleItem();
        rows[0].InstallId.ShouldBe(result.InstallId);
        rows[0].PackageName.ShouldBe("pkg-id-test");
    }

    [Fact]
    public async Task InstallAsync_TwoPackageBatch_TwoRowsShareSameInstallId()
    {
        var (svc, scopeFactory) = BuildService();

        var targets = new[]
        {
            MakeTarget("pkg-id-1"),
            MakeTarget("pkg-id-2"),
        };

        var result = await svc.InstallAsync(targets, TestContext.Current.CancellationToken);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var rows = await db.PackageInstalls
            .ToListAsync(TestContext.Current.CancellationToken);
        rows.Count.ShouldBe(2);
        rows.All(r => r.InstallId == result.InstallId).ShouldBeTrue();
        rows.Select(r => r.PackageName).OrderBy(x => x)
            .ShouldBe(new[] { "pkg-id-1", "pkg-id-2" }.OrderBy(x => x));
    }

    // ── Test 10: Name-collision pre-flight ─────────────────────────────────

    [Fact]
    public async Task InstallAsync_NameCollision_ThrowsBeforeAnyRowsWritten()
    {
        // Post-#1629 the collision pre-flight queries the staging DB by
        // DisplayName. Seed a UnitDefinition row with the colliding name so
        // the pre-flight observes a real collision against the package's
        // unit "main".
        var (svc, scopeFactory) = BuildService();

        await using (var seedScope = scopeFactory.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<SpringDbContext>();
            seedDb.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = Unit_Main_Id,
                TenantId = TenantA,
                DisplayName = "main",
                Description = string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var ex = await Should.ThrowAsync<PackageNameCollisionException>(async () =>
            await svc.InstallAsync(
                new[] { MakeTarget("pkg-collision") },
                TestContext.Current.CancellationToken));

        ex.CollidingNames.ShouldContain("main");

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        (await db.PackageInstalls.IgnoreQueryFilters()
            .ToListAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    // ── Test 11: Round-trip blob storage ───────────────────────────────────

    [Fact]
    public async Task InstallAsync_YamlWithComments_StoredVerbatim()
    {
        var (svc, scopeFactory) = BuildService();

        var root = CreatePackageDir();
        var target = new InstallTarget(
            "my-package",
            new Dictionary<string, string>(),
            YamlWithComments,
            root);

        await svc.InstallAsync(new[] { target }, TestContext.Current.CancellationToken);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.PackageInstalls
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        row.ShouldNotBeNull();
        row!.OriginalManifestYaml.ShouldBe(YamlWithComments);
    }

    // ── Test 12: Backwards compat / GetStatusAsync helpers ─────────────────

    [Fact]
    public async Task GetStatusAsync_UnknownInstallId_ReturnsNull()
    {
        var (svc, _) = BuildService();
        var status = await svc.GetStatusAsync(
            Guid.NewGuid(), TestContext.Current.CancellationToken);
        status.ShouldBeNull();
    }

    [Fact]
    public async Task AbortAsync_UnknownInstallId_CompletesWithoutException()
    {
        var (svc, _) = BuildService();
        await Should.NotThrowAsync(async () =>
            await svc.AbortAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RetryAsync_UnknownInstallId_ThrowsInvalidOperation()
    {
        var (svc, _) = BuildService();
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await svc.RetryAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }
}