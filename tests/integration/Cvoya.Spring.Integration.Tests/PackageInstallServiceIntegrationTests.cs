// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Host.Api.Services;
using Cvoya.Spring.Manifest;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// End-to-end coverage for <see cref="PackageInstallService"/> against the
/// real in-repo packages (#2311). The tests stand up the Dapr DI graph
/// with an in-memory <see cref="SpringDbContext"/>, point the catalog at
/// <c>packages/</c> in the repo root, and substitute
/// <see cref="IActorProxyFactory"/> with a fake that delegates
/// <see cref="IUnitActor.AddMemberAsync"/> calls to a real
/// <see cref="IUnitMembershipCoordinator"/> — so the activation pipeline
/// writes <c>unit_memberships</c> rows through the same path the runtime
/// uses, without standing up a live Dapr placement.
/// </summary>
/// <remarks>
/// Pattern mirrors <see cref="DefaultTenantBootstrapTests"/>: in-memory
/// EF, DI-graph composition, no Testcontainers. The tests are
/// deliberately conservative — they verify the directory rows + the
/// Guid-shape of the agent address (the regression #2309 fixed) and the
/// membership wiring. Actor-runtime side-effects (dispatcher launch,
/// lifecycle transitions) are out of scope for the install pipeline.
/// </remarks>
public class PackageInstallServiceIntegrationTests : IDisposable
{
    private readonly string _packagesRoot;
    private readonly List<ServiceProvider> _providers = new();

    public PackageInstallServiceIntegrationTests()
    {
        _packagesRoot = LocateRepoPackagesRoot();
    }

    public void Dispose()
    {
        foreach (var p in _providers)
        {
            try { p.Dispose(); } catch { /* best effort */ }
        }
    }

    // ── (1) hello-world ──────────────────────────────────────────────────

    [Fact]
    public async Task InstallHelloWorld_HappyPath()
    {
        using var fx = BuildFixture();

        var result = await fx.Service.InstallAsync(
            new[] { LoadTarget("hello-world") },
            TestContext.Current.CancellationToken);

        result.PackageResults.Single().Status.ShouldBe(PackageInstallOutcome.Active);

        await using var scope = fx.ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // hello-world Unit row
        var unitRow = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .Where(u => u.DisplayName == "hello-world")
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        unitRow.ShouldNotBeNull("hello-world unit row should be written by the install");
        unitRow!.Id.ShouldNotBe(Guid.Empty);

        // greeter Agent row
        var agentRow = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .Where(a => a.DisplayName == "greeter")
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        agentRow.ShouldNotBeNull("greeter agent row should be written by the install");
        agentRow!.Id.ShouldNotBe(Guid.Empty);

        // Agent's directory address must be Guid-formatted, not slug-formatted
        // (the regression #2309 fixed — Address.For(slug) would explode on a
        // non-Guid path).
        var agentAddress = Address.ForIdentity(Address.AgentScheme, agentRow.Id);
        agentAddress.Path.ShouldBe(GuidFormatter.Format(agentRow.Id));
        agentAddress.Path.ShouldNotContain("-");

        // unit_memberships row links the agent to the unit.
        var membership = await db.UnitMemberships
            .IgnoreQueryFilters()
            .Where(m => m.UnitId == unitRow.Id && m.AgentId == agentRow.Id)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        membership.ShouldNotBeNull(
            "unit_memberships row should link the greeter agent to the hello-world unit");
    }

    // ── (2) example-simple ───────────────────────────────────────────────

    [Fact]
    public async Task InstallExampleSimple_HappyPath()
    {
        using var fx = BuildFixture();

        var result = await fx.Service.InstallAsync(
            new[] { LoadTarget("example-simple") },
            TestContext.Current.CancellationToken);

        result.PackageResults.Single().Status.ShouldBe(PackageInstallOutcome.Active);

        await using var scope = fx.ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // 1 unit
        var unitRows = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .Where(u => u.DisplayName == "greeting-team")
            .ToListAsync(TestContext.Current.CancellationToken);
        unitRows.Count.ShouldBe(1);
        var unitId = unitRows[0].Id;

        // 2 agents
        var friendly = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.DisplayName == "friendly-greeter",
                TestContext.Current.CancellationToken);
        friendly.ShouldNotBeNull();
        var polite = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.DisplayName == "polite-greeter",
                TestContext.Current.CancellationToken);
        polite.ShouldNotBeNull();
        friendly!.Id.ShouldNotBe(polite!.Id);

        // Each agent has a unit_memberships row pointing at the greeting-team
        // unit (the regression #2309 guard — pre-fix slug-resolution would
        // have minted a different unit-id and stranded the agents).
        var memberships = await db.UnitMemberships
            .IgnoreQueryFilters()
            .Where(m => m.UnitId == unitId)
            .ToListAsync(TestContext.Current.CancellationToken);
        memberships.Count.ShouldBe(2,
            "the unit should resolve both members to its own Guid, not to phantom ids");
        memberships.Select(m => m.AgentId).ShouldBe(
            new[] { friendly.Id, polite.Id }, ignoreOrder: true);
    }

    // ── (3) example-templated ────────────────────────────────────────────

    [Fact]
    public async Task InstallExampleTemplated_HappyPath()
    {
        using var fx = BuildFixture();

        var result = await fx.Service.InstallAsync(
            new[] { LoadTarget("example-templated") },
            TestContext.Current.CancellationToken);

        result.PackageResults.Single().Status.ShouldBe(PackageInstallOutcome.Active);

        await using var scope = fx.ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // Top-level unit: platform-eng (stamped from the engineering-team
        // template — the concrete unit's name wins per ADR-0043 §5d).
        var platformEng = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.DisplayName == "platform-eng",
                TestContext.Current.CancellationToken);
        platformEng.ShouldNotBeNull("platform-eng unit row should exist");

        // 5 stamped agents: team-lead + senior-engineer from the template,
        // plus ada / hopper / lovelace declared in the concrete unit's
        // agents/ folder (all three `from: software-engineer`).
        var agents = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .ToListAsync(TestContext.Current.CancellationToken);
        var expectedNames = new[] { "team-lead", "senior-engineer", "ada", "hopper", "lovelace" };
        foreach (var name in expectedNames)
        {
            agents.Any(a => a.DisplayName == name).ShouldBeTrue(
                $"agent '{name}' should be present after stamping");
        }

        // Each stamped software-engineer instance has a fresh Guid — no
        // duplicates across ada/hopper/lovelace (the stamping must produce
        // distinct identities).
        var stamped = agents
            .Where(a => expectedNames.Contains(a.DisplayName))
            .Select(a => a.Id)
            .ToList();
        stamped.Distinct().Count().ShouldBe(stamped.Count,
            "every stamped agent must have a distinct Guid");
    }

    // ── (4) install twice — both succeed ─────────────────────────────────

    [Fact]
    public async Task InstallTwice_SamePackage_BothSucceed()
    {
        using var fx = BuildFixture();

        var first = await fx.Service.InstallAsync(
            new[] { LoadTarget("hello-world") },
            TestContext.Current.CancellationToken);
        first.PackageResults.Single().Status.ShouldBe(PackageInstallOutcome.Active);

        var second = await fx.Service.InstallAsync(
            new[] { LoadTarget("hello-world") },
            TestContext.Current.CancellationToken);
        second.PackageResults.Single().Status.ShouldBe(PackageInstallOutcome.Active);

        first.InstallId.ShouldNotBe(second.InstallId);

        await using var scope = fx.ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // Two distinct unit rows with the same display name (#2310: names are
        // presentation-only; identity is Guid).
        var unitRows = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .Where(u => u.DisplayName == "hello-world")
            .ToListAsync(TestContext.Current.CancellationToken);
        unitRows.Count.ShouldBe(2);
        unitRows[0].Id.ShouldNotBe(unitRows[1].Id);

        // Two distinct agent rows.
        var agentRows = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .Where(a => a.DisplayName == "greeter")
            .ToListAsync(TestContext.Current.CancellationToken);
        agentRows.Count.ShouldBe(2);
        agentRows[0].Id.ShouldNotBe(agentRows[1].Id);
    }

    // ── (5) display-name override ────────────────────────────────────────

    [Fact]
    public async Task InstallWithDisplayName_OverridesUnitName()
    {
        using var fx = BuildFixture();

        var target = LoadTarget("hello-world") with { DisplayName = "my-greeting-team" };
        var result = await fx.Service.InstallAsync(
            new[] { target },
            TestContext.Current.CancellationToken);

        result.PackageResults.Single().Status.ShouldBe(PackageInstallOutcome.Active);

        await using var scope = fx.ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var unit = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.DisplayName == "my-greeting-team",
                TestContext.Current.CancellationToken);
        unit.ShouldNotBeNull("the unit should carry the operator-supplied display name");

        // The original "hello-world" display name must NOT appear — the
        // override wins.
        var originalNameRow = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.DisplayName == "hello-world",
                TestContext.Current.CancellationToken);
        originalNameRow.ShouldBeNull(
            "the package's declared 'hello-world' name should be replaced by the override");
    }

    // ── (6) ambiguous override rejection ─────────────────────────────────

    [Fact]
    public async Task InstallWithDisplayName_AmbiguousMultipleTopLevel_Rejected()
    {
        using var fx = BuildFixture();

        // Build a small in-fixture package with two top-level units so the
        // ambiguity check fires. Fixture lives under a per-test temp dir so
        // multiple parallel runs don't clobber each other.
        using var multi = BuildMultiTopLevelPackage();

        var target = new InstallTarget(
            PackageName: multi.PackageName,
            Inputs: new Dictionary<string, string>(),
            OriginalYaml: multi.PackageYaml,
            PackageRoot: multi.PackageRoot,
            DisplayName: "rename-me");

        var ex = await Should.ThrowAsync<AmbiguousDisplayNameException>(async () =>
            await fx.Service.InstallAsync(
                new[] { target },
                TestContext.Current.CancellationToken));

        ex.PackageName.ShouldBe(multi.PackageName);
        ex.TopLevelCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    // ── (7) malformed manifest fails cleanly ─────────────────────────────

    [Fact]
    public async Task InstallMalformedManifest_FailsCleanly()
    {
        using var fx = BuildFixture();

        // `kind: Pakage` (typo) — ADR-0043 §3 rejects unknown kinds with a
        // precise parse error code.
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Pakage
            name: typo-pkg
            description: x
            version: 1.0.0
            """;
        var target = new InstallTarget(
            PackageName: "typo-pkg",
            Inputs: new Dictionary<string, string>(),
            OriginalYaml: yaml,
            PackageRoot: null);

        await Should.ThrowAsync<PackageParseException>(async () =>
            await fx.Service.InstallAsync(
                new[] { target },
                TestContext.Current.CancellationToken));
    }

    // ── Fixture helpers ──────────────────────────────────────────────────

    private sealed class Fixture : IDisposable
    {
        public required PackageInstallService Service { get; init; }
        public required IServiceScopeFactory ScopeFactory { get; init; }
        public required ServiceProvider Provider { get; init; }

        public void Dispose() => Provider.Dispose();
    }

    private Fixture BuildFixture()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // UnitCreationService depends on IHttpContextAccessor for the
        // creator-grant path (line 684 of UnitCreationService.cs).
        // Register the standard implementation; the tests don't push a
        // request through HTTP so the accessor's Current is null and the
        // service falls back to its anonymous-creator branch.
        services.AddHttpContextAccessor();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tenancy:BootstrapDefaultTenant"] = "false",
                ["Packages:Root"] = _packagesRoot,
                ["Skills:PackagesRoot"] = _packagesRoot,
            })
            .Build();
        services.AddSingleton<IConfiguration>(config);

        // In-memory EF, fresh per test.
        var dbName = $"sv-install-{Guid.NewGuid():N}";
        services.AddDbContext<SpringDbContext>(opts => opts
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

        // Substitute the actor proxy factory with a fake that delegates
        // IUnitActor.AddMemberAsync to the real IUnitMembershipCoordinator
        // (resolved from DI after the provider is built) so unit_memberships
        // rows are actually written. Every other actor method is a no-op
        // substitute — the install pipeline's directory-state assertions
        // don't depend on actor-state writes.
        var actorProxyFactory = new TestActorProxyFactory();
        services.AddSingleton<IActorProxyFactory>(actorProxyFactory);

        // Wire the Dapr DI graph — pulls in IUnitMemberGraphStore,
        // IUnitMembershipCoordinator, IDirectoryService, etc. This also
        // registers an IActorProxyFactory implementation, but TryAdd
        // semantics mean ours wins.
        services.AddCvoyaSpringDapr(config);

        // Wire the Host.Api install pipeline + catalog. Pinning
        // PackageCatalogOptions before AddCvoyaSpringApiServices ensures
        // the catalog points at the in-repo packages/ root (the
        // extension's auto-discovery is best-effort).
        services.AddSingleton<PackageCatalogOptions>(_ => new PackageCatalogOptions
        {
            Root = _packagesRoot,
        });
        services.AddCvoyaSpringApiServices(config);

        var sp = services.BuildServiceProvider();
        _providers.Add(sp);
        actorProxyFactory.Bind(sp);

        return new Fixture
        {
            Service = (PackageInstallService)sp.GetRequiredService<IPackageInstallService>(),
            ScopeFactory = sp.GetRequiredService<IServiceScopeFactory>(),
            Provider = sp,
        };
    }

    private InstallTarget LoadTarget(string packageName)
    {
        var packageRoot = Path.Combine(_packagesRoot, packageName);
        var yaml = File.ReadAllText(Path.Combine(packageRoot, "package.yaml"));
        return new InstallTarget(
            PackageName: packageName,
            Inputs: new Dictionary<string, string>(),
            OriginalYaml: yaml,
            PackageRoot: packageRoot);
    }

    private sealed class MultiTopLevelPackage : IDisposable
    {
        public required string PackageRoot { get; init; }
        public required string PackageName { get; init; }
        public required string PackageYaml { get; init; }

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

    private static MultiTopLevelPackage BuildMultiTopLevelPackage()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "spring-voyage-tests",
            $"multi-top-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        const string packageName = "multi-top-level";
        var packageYaml = $"""
            apiVersion: spring.voyage/v1
            kind: Package
            name: {packageName}
            description: Two top-level units — used to trigger the AmbiguousDisplayName check.
            version: 1.0.0
            """;
        File.WriteAllText(Path.Combine(root, "package.yaml"), packageYaml);

        // Two top-level units side-by-side under units/.
        var oneDir = Path.Combine(root, "units", "alpha");
        Directory.CreateDirectory(oneDir);
        File.WriteAllText(Path.Combine(oneDir, "package.yaml"), """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: alpha
            description: First top-level unit.
            """);

        var twoDir = Path.Combine(root, "units", "beta");
        Directory.CreateDirectory(twoDir);
        File.WriteAllText(Path.Combine(twoDir, "package.yaml"), """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: beta
            description: Second top-level unit.
            """);

        return new MultiTopLevelPackage
        {
            PackageRoot = root,
            PackageName = packageName,
            PackageYaml = packageYaml,
        };
    }

    private static string LocateRepoPackagesRoot()
    {
        // Mirror the LocatePackageRoot pattern used by HelloWorldPackageTests.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "packages");
            if (Directory.Exists(candidate)
                && File.Exists(Path.Combine(candidate, "hello-world", "package.yaml")))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the repo's packages/ directory by walking up from the test binary location.");
    }

    // ── Test actor proxy factory ─────────────────────────────────────────

    /// <summary>
    /// Test substitute for <see cref="IActorProxyFactory"/>. Returns
    /// NSubstitute proxies for every actor interface; for
    /// <see cref="IUnitActor"/> the proxy's <c>AddMemberAsync</c> is wired
    /// to delegate to the real <see cref="IUnitMembershipCoordinator"/> so
    /// the install pipeline writes <c>unit_memberships</c> rows through
    /// the same path the runtime uses, without standing up a live Dapr
    /// placement.
    /// </summary>
    private sealed class TestActorProxyFactory : IActorProxyFactory
    {
        private IServiceProvider? _services;

        public void Bind(IServiceProvider services) => _services = services;

        public TActorInterface CreateActorProxy<TActorInterface>(
            ActorId actorId,
            string actorType,
            ActorProxyOptions? options = null)
            where TActorInterface : IActor
        {
            ArgumentNullException.ThrowIfNull(actorId);

            if (typeof(TActorInterface) == typeof(IUnitActor))
            {
                var coordinator = _services!.GetRequiredService<IUnitMembershipCoordinator>();
                var unitId = ParseGuid(actorId.GetId());
                var unitAddress = Address.ForIdentity(Address.UnitScheme, unitId);

                var sub = (IUnitActor)Substitute.For(new[] { typeof(IUnitActor) }, Array.Empty<object>());
                sub.AddMemberAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
                    .Returns(call => coordinator.AddMemberAsync(
                        unitId: unitId,
                        unitAddress: unitAddress,
                        member: call.ArgAt<Address>(0),
                        emitStateChanged: (_, _, _) => Task.CompletedTask,
                        cancellationToken: call.ArgAt<CancellationToken>(1)));
                return (TActorInterface)(object)sub;
            }

            // Every other actor interface: a bare substitute is enough —
            // the install pipeline's directory-state assertions don't
            // depend on those methods doing anything. Use the array-typed
            // Substitute.For so the generic constraint check stays happy
            // (TActorInterface is IActor, not necessarily `class`).
            return (TActorInterface)Substitute.For(new[] { typeof(TActorInterface) }, Array.Empty<object>());
        }

        public ActorProxy Create(
            ActorId actorId,
            string actorType,
            ActorProxyOptions? options = null) =>
            throw new NotSupportedException(
                "The test actor proxy factory only supports typed CreateActorProxy<T> calls.");

        public object CreateActorProxy(
            ActorId actorId,
            Type actorInterfaceType,
            string actorType,
            ActorProxyOptions? options = null) =>
            throw new NotSupportedException(
                "The test actor proxy factory only supports the generic overload.");

        private static Guid ParseGuid(string actorId) =>
            GuidFormatter.TryParse(actorId, out var guid) ? guid : Guid.Parse(actorId);
    }
}
