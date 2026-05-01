// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Worker.Tests;

using System.Reflection;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Dapr.Workflows;
using Cvoya.Spring.Host.Worker.Composition;

using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
    /// with "Workflow 'UnitValidationWorkflow' not found in registry"
    /// (IsNonRetriable = true), leaving units stuck in Validating forever.
    ///
    /// The fix: <c>AddWorkerServices</c> calls <c>AddDaprWorkflow</c> with the
    /// full registration list BEFORE calling <c>AddCvoyaSpringDapr</c>.
    /// This test verifies the factory knows about <c>UnitValidationWorkflow</c>
    /// after composition, using reflection to inspect the internal factory
    /// dictionary because <c>IWorkflowsFactory</c> is internal to the SDK.
    /// </summary>
    [Fact]
    public void AddWorkerServices_WorkflowsFactory_ContainsUnitValidationWorkflow()
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
        // to confirm UnitValidationWorkflow is registered. The field name is
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
            nameof(UnitValidationWorkflow),
            $"UnitValidationWorkflow must be registered in the workflow factory. " +
            $"Registered workflows: [{string.Join(", ", keyList)}]. " +
            $"This is a regression of issue #1452: AddDaprWorkflow with the full " +
            $"registration list must run before AddCvoyaSpringDapr.");
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

        builder.Services.AddDbContext<SpringDbContext>(options =>
            options.UseInMemoryDatabase($"WorkerCompositionTest_{Guid.NewGuid()}"));

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