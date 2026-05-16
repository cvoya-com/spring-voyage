// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Worker.Tests;

using System.Reflection;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Dapr.Tenancy;
using Cvoya.Spring.Dapr.Units;
using Cvoya.Spring.Dapr.Workflows;
using Cvoya.Spring.Host.Worker.Composition;

using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Shouldly;

using Xunit;

/// <summary>
/// Smoke test for the Worker host's DI composition (issue #586).
/// </summary>
/// <remarks>
/// <para>
/// The Worker used to have a class of failure that was invisible at
/// <c>dotnet build</c> time and only surfaced at container startup: a
/// registration gap in <c>Host.Worker</c>'s DI graph. The most recent
/// incident (issue #584 / PR #588) was <c>WebSearchSkill</c> asking for
/// <c>IUnitConnectorConfigStore</c> — the default implementation was
/// registered only in <c>Host.Api</c>, so the Worker failed to start for
/// 42+ hours in production while the build stayed green.
/// </para>
/// <para>
/// This test exercises the exact composition helper that <c>Program.cs</c>
/// calls (<see cref="WorkerComposition.AddWorkerServices"/>) and builds the
/// resulting <see cref="IServiceProvider"/> with both
/// <see cref="ServiceProviderOptions.ValidateOnBuild"/> and
/// <see cref="ServiceProviderOptions.ValidateScopes"/> enabled. Together
/// those options construct every singleton during <c>BuildServiceProvider</c>
/// and detect captive-dependency shapes that only explode in production.
/// Any missing transitive registration surfaces here as a failed test
/// instead of a late-night outage.
/// </para>
/// <para>
/// The test runs against an in-memory EF Core provider (the Worker's
/// <c>AddCvoyaSpringDapr</c> fail-fasts when no provider is configured —
/// see #261) and strips the Dapr Workflow worker (no sidecar — see #568),
/// so no external runtime is required. Every other registration — skills,
/// connectors, actors, hosted services, options binding — goes through the
/// same path as the real Worker.
/// </para>
/// </remarks>
public class WorkerCompositionTests
{
    [Fact]
    public void AddWorkerServices_BuildsProviderWithoutMissingRegistrations()
    {
        using var provider = BuildWorkerServiceProvider();

        provider.ShouldNotBeNull();
    }

    [Fact]
    public void AddWorkerServices_EverySkillRegistryResolves()
    {
        using var provider = BuildWorkerServiceProvider();

        var registries = provider.GetServices<ISkillRegistry>().ToList();

        // ExpertiseSkillRegistry + DirectorySearchSkillRegistry from the Dapr
        // module plus one per connector package (GitHub / Arxiv / WebSearch)
        // — a non-empty set is the contract the Worker depends on.
        registries.ShouldNotBeEmpty();
        registries.ShouldAllBe(r => r != null);
    }

    /// <summary>
    /// Regression test for issue #2359: the unit-start connector dispatcher
    /// must resolve in the Worker host. Pre-fix, the registration lived in
    /// <c>Cvoya.Spring.Host.Api.Services.ServiceCollectionExtensions</c>
    /// only, and the Worker — which never invokes <c>AddCvoyaSpringApiServices</c>
    /// — silently saw a null dispatcher on <see cref="Actors.UnitActor"/>.
    /// <c>UnitActor.TryAutoStartAsync</c> exited early on the null check and
    /// every unit settled in <c>Stopped</c> rather than advancing to
    /// <c>Running</c> after validation. Moving the registration into the
    /// shared <c>AddCvoyaSpringDapr</c> module (via
    /// <c>ServiceCollectionExtensionsRouting</c>) fixes the gap.
    /// </summary>
    [Fact]
    public void AddWorkerServices_UnitConnectorStartDispatcher_Resolves()
    {
        using var provider = BuildWorkerServiceProvider();

        var dispatcher = provider.GetService<IUnitConnectorStartDispatcher>();

        dispatcher.ShouldNotBeNull(
            "IUnitConnectorStartDispatcher must resolve in the Worker DI graph; " +
            "without it, UnitActor.TryAutoStartAsync silently aborts the " +
            "Stopped → Starting → Running sequence and units stay wedged in Stopped. " +
            "Regression for issue #2359.");
    }

    /// <summary>
    /// Regression test for #2364: the shared validation pipeline (workflow
    /// scheduler, coordinator, tracker) must resolve in the Worker DI graph
    /// so both <see cref="Actors.UnitActor"/> and <see cref="Actors.AgentActor"/>
    /// can schedule + complete validation. The rename in #2371
    /// (<c>UnitValidation*</c> → <c>ArtefactValidation*</c>) means any host
    /// that missed an updated registration would surface here.
    /// </summary>
    [Fact]
    public void AddWorkerServices_ArtefactValidationPipeline_Resolves()
    {
        using var provider = BuildWorkerServiceProvider();

        var scheduler = provider.GetService<Cvoya.Spring.Core.Lifecycle.IArtefactValidationWorkflowScheduler>();
        var coordinator = provider.GetService<Cvoya.Spring.Core.Lifecycle.IArtefactValidationCoordinator>();
        var tracker = provider.GetService<Cvoya.Spring.Core.Lifecycle.IArtefactValidationTracker>();

        scheduler.ShouldNotBeNull(
            "IArtefactValidationWorkflowScheduler must resolve in the Worker DI graph; " +
            "without it, transitions into Validating silently no-op and no workflow runs. " +
            "Regression guard for the #2371 rename.");
        coordinator.ShouldNotBeNull(
            "IArtefactValidationCoordinator must resolve in the Worker DI graph; " +
            "both UnitActor.TransitionAsync(Validating) and AgentActor.TransitionAsync(Validating) " +
            "delegate to it.");
        tracker.ShouldNotBeNull(
            "IArtefactValidationTracker must resolve in the Worker DI graph; " +
            "the coordinator uses it for the stale-run guard on Validating completion callbacks.");
    }

    [Fact]
    public void AddWorkerServices_EveryConnectorTypeResolves()
    {
        using var provider = BuildWorkerServiceProvider();

        var connectors = provider.GetServices<IConnectorType>().ToList();

        // GitHub / Arxiv / WebSearch all register their IConnectorType in
        // the Worker via AddWorkerServices. The missing-IUnitConnectorConfigStore
        // bug that motivated this test (#586) would surface here: the
        // WebSearchConnectorType singleton pulls the skill, which pulls the
        // store — no store registration, no connector resolution.
        connectors.ShouldNotBeEmpty();
        connectors.ShouldAllBe(c => c != null);
    }

    /// <summary>
    /// Regression test for issue #1452: the Dapr Workflow SDK uses
    /// <c>TryAddSingleton</c> for <c>IWorkflowsFactory</c>, so the FIRST
    /// call to <c>AddDaprWorkflow</c> wins. <c>AddCvoyaSpringDapr</c> (called
    /// from <c>AddWorkerServices</c>) itself calls
    /// <c>AddDaprWorkflow(options =&gt; {})</c> with no registrations as part
    /// of shared infrastructure setup. If that empty call runs before the
    /// Worker's full-registration call, the factory ends up with zero
    /// workflows: every <c>ScheduleNewWorkflowAsync</c> succeeds (the sidecar
    /// accepts the schedule) but the Worker immediately fails the orchestration
    /// with "Workflow 'ArtefactValidationWorkflow' not found in registry"
    /// (IsNonRetriable = true), leaving units stuck in Validating forever.
    ///
    /// The fix: <c>AddWorkerServices</c> calls <c>AddDaprWorkflow</c> with the
    /// full registration list BEFORE calling <c>AddCvoyaSpringDapr</c>.
    /// This test verifies the factory knows about <c>ArtefactValidationWorkflow</c>
    /// after composition, using reflection to inspect the internal factory
    /// dictionary because <c>IWorkflowsFactory</c> is internal to the SDK.
    /// </summary>
    [Fact]
    public void AddWorkerServices_WorkflowsFactory_ContainsArtefactValidationWorkflow()
    {
        using var provider = BuildWorkerServiceProvider();

        // IWorkflowsFactory is internal to Dapr.Workflow; resolve it by
        // walking the service descriptors for the exact interface type.
        var daprWorkflowAssembly = typeof(global::Dapr.Workflow.DaprWorkflowClient).Assembly;
        var factoryInterfaceType = daprWorkflowAssembly
            .GetType("Dapr.Workflow.Worker.IWorkflowsFactory", throwOnError: false);

        factoryInterfaceType.ShouldNotBeNull(
            "IWorkflowsFactory must exist in the Dapr.Workflow assembly for this test to be meaningful");

        var factory = provider.GetService(factoryInterfaceType!);
        factory.ShouldNotBeNull(
            "IWorkflowsFactory must be resolvable from the Worker's DI container");

        // Inspect the internal _workflowFactories dictionary via reflection
        // to confirm ArtefactValidationWorkflow is registered. The field name is
        // stable across the SDK versions we depend on (1.17.x).
        var factoryField = factory.GetType().GetField(
            "_workflowFactories",
            BindingFlags.NonPublic | BindingFlags.Instance);

        factoryField.ShouldNotBeNull(
            "_workflowFactories field must exist on WorkflowsFactory for this test to be meaningful");

        var dict = factoryField!.GetValue(factory) as System.Collections.Concurrent.ConcurrentDictionary<string, object>;

        // The dictionary is ConcurrentDictionary<string, Func<IServiceProvider, IWorkflow>>,
        // but since Func<,> is not easily constrained here, check via the non-generic IDictionary.
        var dictAsNonGeneric = factoryField.GetValue(factory);
        dictAsNonGeneric.ShouldNotBeNull();

        // Use the IDictionary interface available on ConcurrentDictionary<,>
        // to enumerate keys without needing to know the value type at compile time.
        var keysProperty = dictAsNonGeneric.GetType().GetProperty("Keys");
        keysProperty.ShouldNotBeNull();
        var keys = keysProperty!.GetValue(dictAsNonGeneric) as System.Collections.IEnumerable;
        keys.ShouldNotBeNull();

        var keyList = keys!.Cast<object>().Select(k => k.ToString()!).ToList();
        keyList.ShouldContain(
            nameof(ArtefactValidationWorkflow),
            $"ArtefactValidationWorkflow must be registered in the workflow factory. " +
            $"Registered workflows: [{string.Join(", ", keyList)}]. " +
            $"This is a regression of issue #1452: AddDaprWorkflow with the full " +
            $"registration list must run before AddCvoyaSpringDapr.");
    }

    /// <summary>
    /// Regression test: the Worker DI composition must surface the real,
    /// YAML-backed <see cref="IRuntimeCatalog"/> — not the empty fallback
    /// stub that <c>AddCvoyaSpringDapr</c> registers via
    /// <c>TryAddSingleton</c> for test harnesses that omit the catalogue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If <c>AddCvoyaSpringRuntimeCatalog()</c> runs after
    /// <c>AddCvoyaSpringDapr(...)</c> in <see cref="WorkerComposition.AddWorkerServices"/>,
    /// the catalogue's <c>TryAddSingleton</c> is a silent no-op (the
    /// fallback won the race), the
    /// <c>ModelProviderInstallSeedProvider</c> iterates an empty
    /// <c>ModelProviders</c> list, and the default-tenant bootstrap
    /// completes without writing a single
    /// <c>tenant_model_provider_installs</c> row. The portal then fires
    /// "Claude Code requires the anthropic model provider, which is not
    /// installed on this tenant" on every fresh OSS deploy. This test
    /// pins the contract by asserting the catalogue surfaces the four
    /// providers declared in <c>eng/runtime-catalog/runtime-catalog.yaml</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void AddWorkerServices_RuntimeCatalog_SurfacesYamlBackedProviders()
    {
        using var provider = BuildWorkerServiceProvider();

        var catalog = provider.GetRequiredService<IRuntimeCatalog>();

        // The four providers declared in eng/runtime-catalog/runtime-catalog.yaml.
        // Adding/removing a provider in the YAML rightly fails this test
        // — the failure tells the editor to update the assertion.
        catalog.ModelProviders.Select(p => p.Id).ShouldBe(
            ["anthropic", "openai", "google", "ollama"],
            ignoreOrder: true);
    }

    /// <summary>
    /// End-to-end regression for the bootstrap → seed → install pathway.
    /// Spins up the actual <see cref="WorkerComposition.AddWorkerServices"/>
    /// graph (with an in-memory SpringDbContext), runs the
    /// <see cref="DefaultTenantBootstrapService"/>, and asserts every
    /// catalogued model provider lands in
    /// <c>tenant_model_provider_installs</c> against
    /// <see cref="OssTenantIds.Default"/>.
    /// </summary>
    /// <remarks>
    /// Pre-fix this test fails with an empty install list (the empty
    /// fallback catalogue won the registration race). Post-fix every
    /// declared provider is upserted on first run.
    /// </remarks>
    [Fact]
    public async Task AddWorkerServices_DefaultTenantBootstrap_InstallsEveryCataloguedModelProvider()
    {
        using var provider = BuildWorkerServiceProvider();

        var bootstrap = provider.GetServices<IHostedService>()
            .OfType<DefaultTenantBootstrapService>()
            .Single();

        await bootstrap.StartAsync(TestContext.Current.CancellationToken);

        // The catalogue is the source of truth; iterate it so the test
        // adapts when a new provider lands in runtime-catalog.yaml. The
        // bootstrap must persist a row per declared provider.
        var catalog = provider.GetRequiredService<IRuntimeCatalog>();
        var expected = catalog.ModelProviders.Select(p => p.Id).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        expected.ShouldNotBeEmpty(
            "eng/runtime-catalog/runtime-catalog.yaml must declare at least one model provider; " +
            "an empty catalogue is the silent failure mode this test exists to catch.");

        // Resolve a fresh scope so the install service sees the rows
        // committed by the bootstrap's own scope. The query uses
        // IgnoreQueryFilters defensively — the seed writes through the
        // scoped tenant context (OssTenantIds.Default) and the OSS
        // ConfiguredTenantContext resolves to the same id, so the
        // filter would match anyway, but bypassing it keeps the
        // assertion focused on "did the row land?" rather than "is the
        // ambient tenant id agreement also intact?".
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var installed = await db.TenantModelProviderInstalls
            .IgnoreQueryFilters()
            .ToListAsync(TestContext.Current.CancellationToken);

        installed.Select(r => r.ProviderId)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ShouldBe(expected);
        installed.ShouldAllBe(r => r.TenantId == OssTenantIds.Default);
        installed.ShouldAllBe(r => r.DeletedAt == null);
    }

    private static ServiceProvider BuildWorkerServiceProvider()
    {
        var builder = WebApplication.CreateBuilder();

        // Satisfy the #261 fail-fast ConnectionStrings:SpringDb check.
        // AddCvoyaSpringDapr runs inside AddWorkerServices BEFORE the
        // DbContext swap below, so a missing value would throw.
        // The value is never opened — the in-memory registration below
        // supersedes it — it just has to be non-empty.
        builder.Configuration["ConnectionStrings:SpringDb"] =
            "Host=test;Database=test;Username=test;Password=test";

        builder.Services.AddWorkerServices(builder.Configuration);

        // Swap Npgsql for an in-memory EF Core provider. AddCvoyaSpringDapr
        // honours pre-registered DbContextOptions<SpringDbContext>, but since
        // we call it through AddWorkerServices above, the Npgsql provider has
        // already been wired — strip and replace. Mirrors the API tests'
        // CustomWebApplicationFactory.
        var dbDescriptors = builder.Services
            .Where(d => d.ServiceType == typeof(DbContextOptions<SpringDbContext>)
                     || d.ServiceType == typeof(DbContextOptions)
                     || d.ServiceType == typeof(SpringDbContext)
                     || (d.ServiceType.FullName?.StartsWith(
                            "Microsoft.EntityFrameworkCore.", StringComparison.Ordinal) ?? false)
                     || (d.ServiceType.FullName?.StartsWith(
                            "Npgsql.", StringComparison.Ordinal) ?? false))
            .ToList();

        foreach (var descriptor in dbDescriptors)
        {
            builder.Services.Remove(descriptor);
        }

        // Capture the DB name in a local so the options-builder callback —
        // AddDbContext registers DbContextOptions<SpringDbContext> with a
        // Scoped lifetime by default, so the lambda runs once per scope.
        // An inline `Guid.NewGuid()` would mint a fresh in-memory database
        // per scope, which means the bootstrap's child scope writes seed
        // rows to one database and a verification scope reads from
        // another. Mirrors DefaultTenantRecordSeedProviderTests.BuildProvider.
        var dbName = $"WorkerCompositionTest_{Guid.NewGuid():N}";
        builder.Services.AddDbContext<SpringDbContext>(options =>
            options.UseInMemoryDatabase(dbName));

        // No Dapr sidecar in tests — strip the workflow worker background
        // service via the shared helper that also backs #568's integration-
        // test workaround. The DaprWorkflowClient and the rest of the
        // workflow DI graph stay registered so any singleton that depends on
        // them still resolves during ValidateOnBuild.
        builder.Services.RemoveDaprWorkflowWorker();

        // Strip DatabaseMigrator (the Worker-owned hosted service that
        // applies EF migrations). The in-memory provider has no migration
        // history and MigrateAsync would throw; ValidateOnBuild only
        // constructs hosted services, it does not start them — but we
        // also remove it to keep the smoke-test's intent tight: exercise
        // DI, not data-store side effects.
        var migratorDescriptors = builder.Services
            .Where(d => d.ImplementationType == typeof(DatabaseMigrator))
            .ToList();
        foreach (var descriptor in migratorDescriptors)
        {
            builder.Services.Remove(descriptor);
        }

        return builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
