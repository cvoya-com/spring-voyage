// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Configuration;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Issues;
using Cvoya.Spring.Dapr.Threads;
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

        // #2160: operational issues. Single instance backs both the
        // writer (producers) and the reader (Overview / CLI / aggregator)
        // surfaces; the repository scopes per call internally so it can
        // be safely consumed from non-request-scoped callers (workflow
        // activities, actors).
        services.TryAddSingleton<IssueRepository>();
        services.TryAddSingleton<Cvoya.Spring.Core.Issues.IIssueWriter>(
            sp => sp.GetRequiredService<IssueRepository>());
        services.TryAddSingleton<Cvoya.Spring.Core.Issues.IIssueReader>(
            sp => sp.GetRequiredService<IssueRepository>());
        services.TryAddSingleton<Cvoya.Spring.Core.Issues.IIssueAggregator, IssueAggregator>();
        services.TryAddScoped<IUnitSubunitMembershipRepository, UnitSubunitMembershipRepository>();
        services.TryAddScoped<IUnitPolicyRepository, UnitPolicyRepository>();

        // #2044 / ADR-0040: ACL grants live in EF, not actor state. Scoped
        // because the underlying SpringDbContext is per-request; the
        // singleton IUnitHumanPermissionStore wraps a scope-per-call so
        // UnitActor (not request-scoped) can write through this seam.
        services.TryAddScoped<IUnitHumanPermissionRepository, UnitHumanPermissionRepository>();

        // #2048 / ADR-0040: agent live config (model / specialty / enabled
        // / execution_mode), skill grants, and expertise live in EF, not
        // actor state. Scoped repository wrapped by a singleton store so
        // AgentActor (not request-scoped) can write through it.
        services.TryAddScoped<IAgentLiveConfigRepository, AgentLiveConfigRepository>();

        // #2049 / ADR-0040: unit live config (model / color / provider /
        // hosting), boundary, permission-inheritance flag, and
        // own-expertise live in EF, not actor state. Scoped repository
        // wrapped by a singleton store so UnitActor (not request-scoped)
        // can write through it.
        services.TryAddScoped<IUnitLiveConfigRepository, UnitLiveConfigRepository>();

        // #2050 / ADR-0040: unit connector bindings (connector type,
        // typed config, runtime metadata) live in EF on the
        // unit_connector_bindings table, not actor state. Scoped repo
        // wrapped by a singleton store (registered separately) so the
        // unit lifecycle endpoints and the public connector-package
        // surface (IUnitConnectorConfigStore / IUnitConnectorRuntimeStore)
        // can read/write through it from singleton call sites.
        services.TryAddScoped<IUnitConnectorBindingRepository, UnitConnectorBindingRepository>();

        // ADR-0040 / #2052: the EF-backed unit member graph store is the
        // singleton seam UnitActor uses to read / write the
        // unit_memberships + unit_subunit_memberships tables on every
        // member-graph call. UnitActor is not request-scoped and cannot
        // consume scoped repositories directly; the store creates a
        // fresh DI scope per call so the EF context resolves cleanly.
        // TryAddSingleton so the cloud overlay can register a tenant-
        // aware decorator (audit / permission / multi-tenant context)
        // ahead of the OSS default.
        services.TryAddSingleton<IUnitMemberGraphStore, UnitMemberGraphStore>();

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

        // Package-declared human resolution policy (ADR-0044). OSS default
        // auto-fills every declared role with the install caller's UUID;
        // the cloud overlay registers a tenant-aware variant ahead of this
        // line. Singleton — the policy consults only request data, never
        // per-request state. TryAddSingleton keeps the cloud override hook.
        services.TryAddSingleton<Cvoya.Spring.Core.Packages.IPackageHumanResolutionPolicy,
            Auth.OssPackageHumanResolutionPolicy>();

        // Unit team-membership read seam (ADR-0044). Singleton — same
        // shape as IUnitMemberGraphStore (scope-per-call internally). The
        // cloud overlay registers a tenant-aware decorator ahead of this
        // line. TryAddSingleton keeps the cloud override hook.
        services.TryAddSingleton<Cvoya.Spring.Core.Units.IUnitHumanMembershipStore,
            Units.EfUnitHumanMembershipStore>();

        // Participant display-name resolver (#1485). Lifted from Host.Api
        // to Dapr (interface in Core) by #2129 so the prompt-assembly path
        // (ThreadContextBuilder, in this assembly) can fold raw
        // scheme:<guid> sender prefixes down to display names before the
        // text reaches the LLM. Scoped per request so the per-call cache in
        // ParticipantDisplayNameResolver stays request-bounded. TryAddScoped
        // so the cloud overlay can register a tenant-aware implementation
        // ahead of the OSS default.
        services.TryAddScoped<IParticipantDisplayNameResolver, Auth.ParticipantDisplayNameResolver>();

        // Thread registry (#2047 / ADR-0030). Scoped because it consults
        // SpringDbContext which is scoped per request. TryAddScoped so a
        // cloud overlay can register a tenant-aware decorator ahead of the
        // OSS default (e.g. permission / audit-logging wrapper).
        services.TryAddScoped<IThreadRegistry, EfThreadRegistry>();

        // Message writer (#2053 / ADR-0030). Scoped for the same reason as
        // the thread registry — consumes SpringDbContext + ITenantContext
        // via constructor injection. The MessageRouter (singleton) opens a
        // scope per dispatch via IServiceScopeFactory rather than holding a
        // long-lived DbContext. TryAddScoped so the cloud overlay can layer
        // an outbox-pattern decorator ahead of the OSS default if a stricter
        // dispatch-vs-persistence transactional boundary becomes necessary.
        services.TryAddScoped<IMessageWriter, EfMessageWriter>();

        services.AddCvoyaSpringTenantPlugins(configuration);

        return services;
    }
}
