// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Configuration;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Units;

using global::Dapr.Actors.Client;
using global::Dapr.Workflow;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

/// <summary>
/// Core infrastructure registrations: Dapr client, EF Core, configuration
/// validation, database options, repositories, skill bundles, tenant services,
/// and credential health.
/// </summary>
internal static class ServiceCollectionExtensionsInfrastructure
{
    internal static IServiceCollection AddCvoyaSpringInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var isDocGen = BuildEnvironment.IsDesignTimeTooling;

        // Dapr client, actor proxy factory, and workflow client
        services.AddDaprClient();

        // Configure the actor proxy factory to use JSON serialization with
        // shared options that include a JsonElement converter which detaches
        // each parsed element from the transient JsonDocument owned by the
        // deserialization scope. Dapr's default DataContract serializer
        // cannot round-trip Message.Payload (a JsonElement) and leaves it as
        // default(JsonElement), which then crashes ASP.NET Core's response
        // writer with "Operation is not valid due to the current state of
        // the object" — the bug behind the GET /api/v1/agents/{id} 500.
        services.TryAddSingleton<IActorProxyFactory>(_ => new ActorProxyFactory(
            new ActorProxyOptions
            {
                UseJsonSerialization = true,
                JsonSerializerOptions = ActorRemotingJsonOptions.Instance,
            }));

        services.AddDaprWorkflow(options => { });

        // During build-time OpenAPI generation (GetDocument.Insider) the Dapr
        // Workflow hosted service starts a gRPC bidirectional stream with the
        // sidecar. There is no sidecar at build time, so it spams "Connection
        // refused" errors. Strip the worker (keeping DaprWorkflowClient and
        // the rest of the workflow DI graph) via the shared helper that also
        // backs the integration-test workaround for #568. See #370 and #568.
        if (isDocGen)
        {
            services.RemoveDaprWorkflowWorker();
        }

        // EF Core / PostgreSQL.
        //
        // Test harnesses (e.g. CustomWebApplicationFactory) pre-register
        // DbContextOptions<SpringDbContext> via UseInMemoryDatabase BEFORE
        // calling AddCvoyaSpringDapr; we respect that and skip our default
        // Npgsql wiring. Otherwise we bind Npgsql when a connection string
        // is present, or register the context without a provider when one
        // is not. The #616 DatabaseConfigurationRequirement owns the
        // missing / malformed classification and raises a fatal error
        // through the startup validator — we no longer throw from here.
        //
        // Design-time tooling (dotnet-ef, dotnet-getdocument for the
        // build-time OpenAPI document) loads the host without a database
        // connection and never actually opens the context. The absent
        // validator at build-time plus the provider-less registration keep
        // the build-time OpenAPI emitter working with no local database.
        var alreadyRegistered = services.Any(d =>
            d.ServiceType == typeof(DbContextOptions<SpringDbContext>));
        if (!alreadyRegistered)
        {
            var connectionString = configuration.GetConnectionString("SpringDb");
            if (string.IsNullOrEmpty(connectionString))
            {
                // Register the context without a provider so construction
                // succeeds. The DatabaseConfigurationRequirement reports
                // Invalid+Mandatory at StartAsync, aborting boot with a
                // clear message before any EF query runs. Build-time
                // tooling (isDocGen) never resolves the context.
                services.AddDbContext<SpringDbContext>(_ => { });
            }
            else
            {
                services.AddDbContext<SpringDbContext>(options =>
                    options.UseNpgsql(connectionString, npgsql =>
                        npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "spring")));
            }
        }

        // #616: tier-1 configuration validation framework. Register the
        // validator + reference requirements first so the validator's
        // IHostedService enumerates them before any other hosted service
        // runs. Design-time tooling skips the validator entirely — the
        // build-time OpenAPI emitter never starts the host lifecycle, and
        // the validator would otherwise fail on a provider-less context.
        //
        // #639 adds the subsystem requirements (Dapr state store, secrets,
        // dispatcher, container runtime) alongside the Database reference
        // requirement shipped in #616. They are registered here (rather
        // than next to each subsystem's own option binding below) so
        // AddCvoyaSpringDapr remains the single entry point that wires the
        // full validation set.
        if (!isDocGen)
        {
            services.AddCvoyaSpringConfigurationValidator();
            // Signal to DatabaseConfigurationRequirement whether the caller
            // pre-registered a DbContext (test harness path) — captured at
            // registration time to avoid resolving the scoped
            // DbContextOptions<SpringDbContext> from the root provider.
            services.AddSingleton(new DatabaseConfigurationRequirement.TestHarnessSignal(alreadyRegistered));
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IConfigurationRequirement, DatabaseConfigurationRequirement>());
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IConfigurationRequirement, DaprStateStoreConfigurationRequirement>());
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IConfigurationRequirement, SecretsConfigurationRequirement>());
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IConfigurationRequirement, DispatcherConfigurationRequirement>());
            // Stage 2 of #522 / #1063: ContainerRuntimeConfigurationRequirement
            // (and the underlying ContainerRuntimeOptions binding) is now
            // dispatcher-only — the worker no longer holds a container CLI
            // binding so validating the worker's `ContainerRuntime:RuntimeType`
            // would fail closed on a setting the worker doesn't use.
            // The dispatcher registers it itself in Cvoya.Spring.Dispatcher/Program.cs.
        }

        // Database options. Always bound — both API and Worker hosts (and
        // any private-cloud host that calls AddCvoyaSpringDapr) need to
        // read DatabaseOptions even though, by default, only the Worker
        // actually applies migrations. Migration registration itself is
        // intentionally NOT performed here: see AddCvoyaSpringDatabaseMigrator
        // and the remarks on DatabaseMigrator for why exactly one host in a
        // deployment owns migrations (issue #305).
        services.AddOptions<DatabaseOptions>().BindConfiguration(DatabaseOptions.SectionName);

        // Repositories
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        services.TryAddScoped<IUnitMembershipRepository, UnitMembershipRepository>();
        services.TryAddScoped<IUnitSubunitMembershipRepository, UnitSubunitMembershipRepository>();
        services.TryAddScoped<IUnitPolicyRepository, UnitPolicyRepository>();

        // Singleton write-through wrapper around the scoped sub-unit
        // membership repository (#1154). UnitActor is not request-scoped
        // and cannot consume the scoped repo directly; the projector
        // creates a fresh DI scope per call so the EF context resolves
        // cleanly. TryAddSingleton so the cloud overlay can register a
        // tenant-aware decorator (audit / permission / multi-tenant
        // context) ahead of the OSS default.
        services.TryAddSingleton<IUnitSubunitMembershipProjector, UnitSubunitMembershipProjector>();

        // Tenant-scoping guard for composition + membership writes (#745).
        // Scoped so the guard sees the current request's tenant context —
        // the SpringDbContext it consults captures CurrentTenantId at query
        // time. TryAddScoped so a cloud overlay can layer additional
        // policy (audit logging, permission checks) on top without
        // displacing the OSS default.
        services.TryAddScoped<IUnitMembershipTenantGuard, UnitMembershipTenantGuard>();

        // Parent-required guard for unit-edge removals (review feedback on
        // #744). Scoped for the same reason as the tenant guard: it reads
        // the per-request SpringDbContext (IsTopLevel lookup) and
        // IUnitHierarchyResolver (singleton, but its internals use a
        // per-walk scope). TryAddScoped keeps the cloud overlay hook.
        services.TryAddScoped<IUnitParentInvariantGuard, UnitParentInvariantGuard>();

        // Unit-policy enforcement (#162 / #163). TryAdd so the private cloud
        // repo can pre-register a tenant-scoped / audit-logging wrapper that
        // wraps the OSS default. Scoped because the underlying repositories
        // use SpringDbContext which is scoped per request.
        services.TryAddScoped<IUnitPolicyEnforcer, DefaultUnitPolicyEnforcer>();

        // Human identity resolver: username → UUID / UUID → display name.
        // Scoped per request so it caches both directions for the lifetime
        // of a single HTTP request. TryAddScoped so the cloud overlay can
        // register a tenant-aware implementation ahead of the OSS default.
        services.TryAddScoped<IHumanIdentityResolver, HumanIdentityResolver>();

        services.AddCvoyaSpringTenantPlugins(configuration);

        return services;
    }
}