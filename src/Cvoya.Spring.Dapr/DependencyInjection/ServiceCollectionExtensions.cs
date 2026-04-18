// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Capabilities;
using Cvoya.Spring.Dapr.Cloning;
using Cvoya.Spring.Dapr.Costs;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Initiative;
using Cvoya.Spring.Dapr.Mcp;
using Cvoya.Spring.Dapr.Observability;
using Cvoya.Spring.Dapr.Orchestration;
using Cvoya.Spring.Dapr.Prompts;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.Secrets;
using Cvoya.Spring.Dapr.Skills;
using Cvoya.Spring.Dapr.State;
using Cvoya.Spring.Dapr.Tenancy;

using global::Dapr.Actors.Client;
using global::Dapr.Workflow;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Extension methods for registering Spring Voyage services with the dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers core Spring Voyage abstractions. Currently a no-op placeholder that allows
    /// the host to signal intent and provides a future extension point.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringCore(this IServiceCollection services)
    {
        return services;
    }

    /// <summary>
    /// Registers all Dapr-backed implementations for routing, execution, orchestration, and prompt assembly.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The application configuration, used to resolve the PostgreSQL connection string.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringDapr(this IServiceCollection services, IConfiguration configuration)
    {
        var isDocGen = BuildEnvironment.IsDesignTimeTooling;

        // Dapr client, actor proxy factory, and workflow client
        services.AddDaprClient();
        services.TryAddSingleton<IActorProxyFactory>(_ => new ActorProxyFactory());

        services.AddDaprWorkflow(options => { });

        // During build-time OpenAPI generation (GetDocument.Insider) the Dapr
        // Workflow hosted service starts a gRPC bidirectional stream with the
        // sidecar. There is no sidecar at build time, so it spams "Connection
        // refused" errors. Remove the hosted-service registration added by
        // AddDaprWorkflow while keeping the DI registrations (DaprWorkflowClient
        // etc.) that endpoints depend on. See #370.
        if (isDocGen)
        {
            var workflowWorkerDescriptor = services
                .FirstOrDefault(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                    && d.ImplementationType?.FullName?.Contains("Dapr.Workflow", StringComparison.Ordinal) == true);
            if (workflowWorkerDescriptor is not null)
            {
                services.Remove(workflowWorkerDescriptor);
            }
        }

        // EF Core / PostgreSQL.
        //
        // Fail fast when no provider is configured. A test harness (e.g. the
        // Host.Api integration tests' CustomWebApplicationFactory) registers
        // its own DbContextOptions<SpringDbContext> — typically backed by
        // UseInMemoryDatabase — BEFORE calling AddCvoyaSpringDapr; in that
        // case we respect the pre-registration and skip our default Npgsql
        // wiring. Otherwise the ConnectionStrings:SpringDb entry is
        // mandatory: a missing value used to register a DbContext without a
        // provider and silently explode on the first EF query deep inside a
        // request (see #261). Throwing here surfaces the misconfiguration at
        // host startup with a clear, actionable message.
        //
        // Design-time tooling (dotnet-ef, dotnet-getdocument for the
        // build-time OpenAPI document) loads the host without a database
        // connection and never actually opens the context. Detect those
        // callers and skip provider wiring so the build-time OpenAPI emitter
        // keeps working with no local database configured.
        var alreadyRegistered = services.Any(d =>
            d.ServiceType == typeof(DbContextOptions<SpringDbContext>));
        if (!alreadyRegistered)
        {
            var connectionString = configuration.GetConnectionString("SpringDb");
            if (string.IsNullOrEmpty(connectionString))
            {
                if (isDocGen)
                {
                    // Leave the context unconfigured; design-time tooling never resolves it.
                    services.AddDbContext<SpringDbContext>(_ => { });
                }
                else
                {
                    throw new InvalidOperationException(
                        "No connection string found for SpringDbContext. Set the " +
                        "ConnectionStrings:SpringDb configuration value (environment variable " +
                        "ConnectionStrings__SpringDb=...) to a valid PostgreSQL connection " +
                        "string, or pre-register DbContextOptions<SpringDbContext> before " +
                        "calling AddCvoyaSpringDapr (for example via " +
                        "AddDbContext<SpringDbContext>(options => options.UseInMemoryDatabase(...)) " +
                        "in a test harness).");
                }
            }
            else
            {
                services.AddDbContext<SpringDbContext>(options =>
                    options.UseNpgsql(connectionString, npgsql =>
                        npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "spring")));
            }
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
        services.TryAddScoped<IUnitPolicyRepository, UnitPolicyRepository>();

        // Unit-policy enforcement (#162 / #163). TryAdd so the private cloud
        // repo can pre-register a tenant-scoped / audit-logging wrapper that
        // wraps the OSS default. Scoped because the underlying repositories
        // use SpringDbContext which is scoped per request.
        services.TryAddScoped<IUnitPolicyEnforcer, DefaultUnitPolicyEnforcer>();

        // Skill bundles (#167 / C4). The resolver is a singleton — it reads
        // from disk and caches per-host. The validator is scoped because it
        // depends on IUnitPolicyRepository (which is scoped). TryAdd so the
        // cloud host can register a tenant-scoped bundle store or validator
        // without touching the API layer.
        services.AddOptions<SkillBundleOptions>().BindConfiguration(SkillBundleOptions.SectionName);
        services.TryAddSingleton<ISkillBundleResolver, FileSystemSkillBundleResolver>();
        services.TryAddScoped<ISkillBundleValidator, DefaultSkillBundleValidator>();
        services.TryAddSingleton<IUnitSkillBundleStore, StateStoreBackedUnitSkillBundleStore>();

        // Agents-as-skills surface (#359 — rework of closed #532). The
        // catalog derives the skill surface live from the expertise
        // directory (#487 / #498) rather than from a startup snapshot, so
        // directory mutations (agent gains expertise, unit boundary
        // changes) propagate on the next enumeration. The invoker is the
        // protocol-agnostic seam that skill callers use instead of
        // IMessageRouter directly — the default implementation routes
        // through the bus so the boundary / permission / policy / activity
        // chain runs end-to-end; the future A2A gateway (#539) will slot in
        // here as an alternative implementation. TryAdd* so downstream
        // hosts (test harnesses, tenant-scoped wrappers, #539 gateway) can
        // pre-register their own catalog / invoker and keep it.
        services.TryAddSingleton<IExpertiseSkillCatalog, ExpertiseSkillCatalog>();
        services.TryAddSingleton<ISkillInvoker, MessageRouterSkillInvoker>();
        services.TryAddSingleton<ExpertiseSkillRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISkillRegistry, ExpertiseSkillRegistry>(
            sp => sp.GetRequiredService<ExpertiseSkillRegistry>()));

        // Directory-search meta-skill registry (#542). Advertises
        // `directory/search` alongside the `expertise/*` surface so planners
        // can call it BEFORE any other skill to resolve "I need something
        // that does X" into a concrete slug. Registered via
        // TryAddEnumerable so the cloud host can add its own search registry
        // (e.g. a tenant-scoped variant) without displacing this one.
        services.TryAddSingleton<DirectorySearchSkillRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISkillRegistry, DirectorySearchSkillRegistry>(
            sp => sp.GetRequiredService<DirectorySearchSkillRegistry>()));

        // Unit-membership backfill hosted service (#160 / C2b-1).
        // Gated by Database:BackfillMemberships; idempotent; short-lived.
        // Also gated by doc-gen mode — the service depends on SpringDbContext
        // which may not have a configured provider during build-time OpenAPI
        // generation. See #370.
        if (!isDocGen)
        {
            services.AddHostedService<UnitMembershipBackfillService>();
        }

        // Options
        services.AddOptions<AiProviderOptions>().BindConfiguration(AiProviderOptions.SectionName);
        services.AddOptions<ContainerRuntimeOptions>().BindConfiguration("ContainerRuntime");
        services.AddOptions<DispatcherClientOptions>().BindConfiguration(DispatcherClientOptions.SectionName);
        services.AddOptions<UnitRuntimeOptions>().BindConfiguration(UnitRuntimeOptions.SectionName);
        services.AddOptions<WorkflowOrchestrationOptions>().BindConfiguration("WorkflowOrchestration");

        // Routing
        services.AddSingleton<DirectoryCache>();
        services.TryAddSingleton<IDirectoryService, DirectoryService>();
        services.TryAddSingleton<IAgentProxyResolver, AgentProxyResolver>();
        services.TryAddSingleton<MessageRouter>();
        services.TryAddSingleton<IMessageRouter>(sp => sp.GetRequiredService<MessageRouter>());

        // Expertise aggregation (#412). TryAdd so the private cloud repo can
        // decorate with tenant filters or a different cache implementation
        // without forking the OSS default. The store reads from the
        // existing agent / unit actor state keys — no new persistence.
        services.TryAddSingleton<IExpertiseStore, ActorBackedExpertiseStore>();

        // Boundary store (#413) — backed by the unit actor's own state.
        services.TryAddSingleton<IUnitBoundaryStore, ActorBackedUnitBoundaryStore>();

        // Register the base aggregator as a concrete singleton so the
        // boundary decorator can take a typed inner reference. Tests that
        // want the raw (unfiltered) aggregator can resolve the concrete
        // type directly.
        services.TryAddSingleton<ExpertiseAggregator>();

        // Boundary-filtering decorator wraps the base aggregator by default
        // (#413). Registered with TryAdd so the private cloud repo can pre-
        // register its own IExpertiseAggregator (e.g. a tenant-scoped
        // decorator) and keep it — this registration is skipped. Call sites
        // that resolve IExpertiseAggregator get the boundary-aware view for
        // free; call sites that want the raw aggregator resolve the concrete
        // ExpertiseAggregator instead.
        services.TryAddSingleton<IExpertiseAggregator>(sp =>
            new BoundaryFilteringExpertiseAggregator(
                sp.GetRequiredService<ExpertiseAggregator>(),
                sp.GetRequiredService<IUnitBoundaryStore>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILoggerFactory>()));

        // Expertise directory search (#542). Lexical / full-text default; a
        // private-cloud host or a future OSS follow-up can swap in a
        // Postgres-FTS or embedding-backed implementation by pre-registering
        // an alternative IExpertiseSearch before calling AddCvoyaSpringDapr.
        services.TryAddSingleton<IExpertiseSearch, InMemoryExpertiseSearch>();

        // Seed expertise from persisted AgentDefinition / UnitDefinition YAML
        // on actor activation (#488). TryAdd so a tenant-scoped host can swap
        // in a store-specific reader without forking. The agent/unit actors
        // depend on this via optional resolution so pre-#488 test harnesses
        // that construct actors manually keep working.
        services.TryAddSingleton<IExpertiseSeedProvider, DbExpertiseSeedProvider>();

        // Execution — AnthropicProvider needs HttpClient
        services.AddHttpClient<IAiProvider, AnthropicProvider>();
        services.AddSingleton<IPromptAssembler, PromptAssembler>();
        services.AddSingleton<IPlatformPromptProvider, PlatformPromptProvider>();

        // Container runtime. The worker no longer holds the local container
        // binary; the spring-dispatcher service does. The worker binds a
        // single DispatcherClientContainerRuntime that forwards every call to
        // the dispatcher over HTTP. See docs/architecture/deployment.md
        // ("Dispatcher service") and issue #513. TryAdd so downstream
        // deployments that run the dispatcher in-process (test harnesses,
        // alternative topologies) can pre-register their own IContainerRuntime.
        services.AddHttpClient(DispatcherClientContainerRuntime.HttpClientName);
        services.TryAddSingleton<IContainerRuntime, DispatcherClientContainerRuntime>();
        services.AddSingleton<IDaprSidecarManager, DaprSidecarManager>();
        services.AddSingleton<ContainerLifecycleManager>();
        services.TryAddSingleton<IUnitContainerLifecycle, UnitContainerLifecycle>();
        services.TryAddSingleton<IExecutionDispatcher, A2AExecutionDispatcher>();

        // Agent definition + tool launchers used by A2AExecutionDispatcher.
        services.TryAddSingleton<IAgentDefinitionProvider, DbAgentDefinitionProvider>();
        services.AddSingleton<IAgentToolLauncher, ClaudeCodeLauncher>();
        services.AddSingleton<IAgentToolLauncher, CodexLauncher>();
        services.AddSingleton<IAgentToolLauncher, GeminiLauncher>();
        services.AddSingleton<IAgentToolLauncher, DaprAgentLauncher>();
        services.TryAddSingleton<PersistentAgentRegistry>();
        // Imperative lifecycle service powering the persistent-agent CLI surface
        // (spring agent deploy/status/scale/logs/undeploy — #396). Kept separate
        // from A2AExecutionDispatcher so the turn-dispatch path stays focused on
        // "there is a message to run" and the operator surface stays focused on
        // "there is a container to manage."
        services.TryAddSingleton<PersistentAgentLifecycle>();

        // In-process MCP server — options and singleton always registered so
        // endpoints that depend on IMcpServer resolve correctly during OpenAPI
        // generation. The hosted-service registration (which binds a port and
        // starts the health monitor) is gated by doc-gen mode. See #370.
        services.AddOptions<McpServerOptions>().BindConfiguration(McpServerOptions.SectionName);
        services.TryAddSingleton<McpServer>();
        services.TryAddSingleton<IMcpServer>(sp => sp.GetRequiredService<McpServer>());

        if (!isDocGen)
        {
            services.AddHostedService(sp => sp.GetRequiredService<PersistentAgentRegistry>());
            services.AddHostedService(sp => sp.GetRequiredService<McpServer>());
        }

        // Initiative — use TryAdd so the private repo can override any implementation.
        // The Dapr state-store-backed variants are registered as defaults so policies and
        // budget state survive process restarts and are shared across replicas; the
        // in-memory implementations remain available for tests that register them first.
        services.TryAddSingleton<ICancellationManager, CancellationManager>();
        services.TryAddSingleton<IAgentPolicyStore, DaprStateAgentPolicyStore>();
        services.TryAddSingleton<IInitiativeBudgetTracker, DaprStateInitiativeBudgetTracker>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IInitiativeEngine, InitiativeEngine>();

        // Persistent cloning policy (#416). The repository rides the shared
        // IStateStore seam so no new component wiring is needed and the
        // rows flow through the same tenant-scoped durability story the
        // cloud host layers on every other agent-scoped setting. The
        // default enforcer is scoped because it composes the scoped
        // unit-membership repository — the private cloud repo can layer
        // a tenant-aware decorator via TryAdd without reshaping
        // persistence. The endpoint resolves IAgentCloningPolicyEnforcer
        // from the scoped request container per call.
        services.TryAddSingleton<IAgentCloningPolicyRepository, StateStoreAgentCloningPolicyRepository>();
        services.TryAddScoped<IAgentCloningPolicyEnforcer, DefaultAgentCloningPolicyEnforcer>();

        services.TryAddKeyedSingleton<ICognitionProvider, Tier1CognitionProvider>("tier1");
        services.TryAddKeyedSingleton<ICognitionProvider, Tier2CognitionProvider>("tier2");

        // Reflection-action handlers (#100). Registered via AddSingleton (not
        // TryAdd) because they are part of an enumerable registration —
        // TryAdd on IEnumerable would silently drop duplicates and the
        // registry relies on ordered iteration. The private cloud repo
        // contributes handlers through plain AddSingleton too; the
        // registry resolves duplicates "first wins" so an earlier-registered
        // cloud handler for, say, "send-message" overrides the OSS default.
        services.AddSingleton<IReflectionActionHandler, SendMessageReflectionActionHandler>();
        services.AddSingleton<IReflectionActionHandler, StartConversationReflectionActionHandler>();
        services.AddSingleton<IReflectionActionHandler, RequestHelpReflectionActionHandler>();
        services.TryAddSingleton<IReflectionActionHandlerRegistry, ReflectionActionHandlerRegistry>();

        services.AddOptions<Tier1Options>().BindConfiguration("Initiative:Tier1");
        services.AddHttpClient<Tier1CognitionProvider>();

        // Orchestration
        services.AddKeyedSingleton<IOrchestrationStrategy, AiOrchestrationStrategy>("ai");
        services.AddKeyedSingleton<IOrchestrationStrategy, WorkflowOrchestrationStrategy>("workflow");
        // Label-routed strategy (#389). Scoped keyed registration because it
        // depends on IUnitPolicyRepository (scoped) for per-turn policy reads.
        // Manifest-driven selection of this strategy per unit is tracked as
        // follow-up work; for now hosts that want label routing wire it up
        // explicitly via the keyed registration.
        services.AddKeyedScoped<IOrchestrationStrategy, LabelRoutedOrchestrationStrategy>("label-routed");

        // Unkeyed default: UnitActor (activated by the Dapr runtime via DI) takes an
        // unkeyed IOrchestrationStrategy — provide one so construction succeeds.
        // "ai" is the platform default; private-cloud code can pre-register a
        // different default via TryAdd.
        services.TryAddSingleton<IOrchestrationStrategy>(
            sp => sp.GetRequiredKeyedService<IOrchestrationStrategy>("ai"));

        // Manifest-driven orchestration strategy selection (#491). The
        // provider reads the `orchestration.strategy` key from the persisted
        // UnitDefinition JSON; the resolver combines that with `UnitPolicy.LabelRouting`
        // inference (ADR-0007) and the unkeyed default into a single
        // per-message resolution path that UnitActor consults through
        // IOrchestrationStrategyResolver. Both are TryAdd'd so the private
        // cloud host can swap in tenant-scoped readers without forking.
        services.TryAddSingleton<IOrchestrationStrategyProvider, DbOrchestrationStrategyProvider>();
        services.TryAddSingleton<IOrchestrationStrategyResolver, DefaultOrchestrationStrategyResolver>();


        // Prompt
        services.AddSingleton<UnitContextBuilder>();
        services.AddSingleton<ConversationContextBuilder>();

        // State
        services.AddOptions<DaprStateStoreOptions>().BindConfiguration(DaprStateStoreOptions.SectionName);
        services.AddSingleton<IStateStore, DaprStateStore>();

        // Tenancy + Secrets. TryAdd so the private cloud repo can replace
        // any of these without touching call sites:
        //   - ITenantContext: OSS uses a singleton bound to Secrets:DefaultTenantId;
        //     private cloud swaps in a scoped resolver that reads the tenant
        //     from the authenticated principal.
        //   - ISecretStore: OSS persists plaintext via Dapr state store
        //     (dev-only; no at-rest encryption); private cloud routes writes
        //     to Azure Key Vault via the Dapr secret-store building block.
        //   - ISecretRegistry / ISecretResolver: composed from the above;
        //     decorators layer RBAC and audit logging.
        services.AddOptions<SecretsOptions>().BindConfiguration(SecretsOptions.SectionName);
        services.TryAddSingleton<ITenantContext, ConfiguredTenantContext>();
        services.TryAddSingleton<ISecretsEncryptor, SecretsEncryptor>();
        services.TryAddSingleton<ISecretStore, DaprStateBackedSecretStore>();
        services.TryAddScoped<ISecretRegistry, EfSecretRegistry>();
        services.TryAddScoped<ISecretResolver, ComposedSecretResolver>();
        // ISecretAccessPolicy: OSS default authorizes everything. The
        // private cloud repo replaces this with a tenant-admin / platform-admin
        // check driven by the authenticated principal — the endpoints only
        // depend on the interface, so no endpoint code has to change.
        services.TryAddSingleton<ISecretAccessPolicy, AllowAllSecretAccessPolicy>();

        // Observability
        services.AddSingleton<ActivityEventBus>();
        services.AddSingleton<IActivityEventBus>(sp => sp.GetRequiredService<ActivityEventBus>());
        services.AddOptions<StreamEventPublisherOptions>().BindConfiguration(StreamEventPublisherOptions.SectionName);
        services.AddSingleton<StreamEventPublisher>();
        services.AddSingleton<StreamEventSubscriber>();

        // Per-unit merged activity stream (issue #391). TryAdd so the private
        // cloud repo can decorate with tenant-scoped filtering without
        // touching the endpoint.
        services.TryAddSingleton<IUnitActivityObservable, UnitActivityObservable>();

        // Auth.
        //
        // Permission resolution (#414) is hierarchy-aware — ancestor grants
        // cascade down to descendant units by default, subject to the
        // per-unit UnitPermissionInheritance flag that plays the role of an
        // opaque boundary for the permission layer. The hierarchy resolver
        // is a DI seam so the private cloud repo can swap in a materialized
        // parent index without touching the permission service.
        services.TryAddSingleton<IUnitHierarchyResolver, DirectoryUnitHierarchyResolver>();
        services.TryAddSingleton<IPermissionService, PermissionService>();

        // Costs — scoped query/tracking services always registered for endpoint DI.
        services.AddScoped<ICostQueryService, CostAggregation>();
        services.AddScoped<ICostTracker, CloneCostTracker>();

        // Observability — query services
        services.AddScoped<IActivityQueryService, ActivityQueryService>();
        // Analytics rollups (#457). TryAdd so the private cloud repo can
        // decorate with tenant-scoped filters without forking the OSS default.
        services.TryAddScoped<IAnalyticsQueryService, AnalyticsQueryService>();

        // Conversation projection (#452 / #456). Materialises conversations
        // and inbox rows from the activity-event table — no separate message
        // store yet. TryAdd so the private cloud host can swap in a tenant-
        // scoped implementation without touching the endpoints.
        services.TryAddScoped<IConversationQueryService, ConversationQueryService>();

        // Hosted services that depend on runtime infrastructure (Dapr state store,
        // database). During build-time OpenAPI generation none of this is
        // available, so skip registration to avoid noisy startup errors. See #370.
        if (!isDocGen)
        {
            services.AddHostedService<ActivityEventPersister>();
            services.AddHostedService<CostTracker>();
            services.AddHostedService<BudgetEnforcer>();
        }

        return services;
    }

    /// <summary>
    /// Registers <see cref="DatabaseMigrator"/> as a hosted service so the
    /// containing host applies pending EF Core migrations to
    /// <see cref="SpringDbContext"/> on startup.
    /// </summary>
    /// <remarks>
    /// Call this from <strong>exactly one</strong> host in a deployment.
    /// In the OSS deployment that host is the Worker
    /// (<c>Cvoya.Spring.Host.Worker</c>); the API host
    /// (<c>Cvoya.Spring.Host.Api</c>) intentionally does not register the
    /// migrator. Registering it in multiple hosts that start
    /// concurrently against the same database races on DDL and one host
    /// will crash with <c>42P07: relation "..." already exists</c> (see
    /// issue #305).
    /// <para>
    /// The actual run is still gated by
    /// <see cref="DatabaseOptions.AutoMigrate"/>; operators that prefer
    /// to apply migrations out-of-band (CI/CD, scripted SQL) can leave
    /// the migrator registered and disable it via configuration.
    /// </para>
    /// <para>
    /// <see cref="DatabaseOptions"/> binding lives in
    /// <see cref="AddCvoyaSpringDapr"/> so non-migrating hosts still
    /// observe the configured value.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringDatabaseMigrator(this IServiceCollection services)
    {
        services.AddHostedService<DatabaseMigrator>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="OllamaProvider"/> as the primary <c>IAiProvider</c> when
    /// <c>LanguageModel:Ollama:Enabled=true</c> (or when the caller forces registration
    /// via <paramref name="force"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hosts call this after <see cref="AddCvoyaSpringDapr"/>. It uses <c>TryAdd</c>
    /// patterns so the private cloud host can pre-register a tenant-scoped
    /// <c>IOptions&lt;OllamaOptions&gt;</c> (for per-tenant base URLs) or an alternative
    /// <c>IAiProvider</c> wrapper, and those overrides are preserved.
    /// </para>
    /// <para>
    /// Ollama exposes an OpenAI-compatible <c>/v1/chat/completions</c> endpoint and
    /// requires no API key, so a configurable base URL is the only knob. The OSS
    /// deployment defaults to the in-cluster <c>spring-ollama</c> container; macOS
    /// GPU deployments override <c>BaseUrl</c> to the host-installed Ollama.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">Configuration root used to bind <see cref="OllamaOptions"/>.</param>
    /// <param name="force">When <c>true</c>, registers the provider regardless of the
    /// <c>Enabled</c> flag — useful for test harnesses that always want the Ollama path.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringOllamaLlm(
        this IServiceCollection services,
        IConfiguration configuration,
        bool force = false)
    {
        services.AddOptions<OllamaOptions>().BindConfiguration(OllamaOptions.SectionName);

        var enabled = force || configuration.GetValue<bool>($"{OllamaOptions.SectionName}:Enabled");
        if (!enabled)
        {
            return services;
        }

        // Clear any prior IAiProvider registration. The base AddCvoyaSpringDapr call
        // registers AnthropicProvider via AddHttpClient<IAiProvider, AnthropicProvider>();
        // when Ollama is explicitly enabled we want it to win. The cloud host can
        // pre-register its own IAiProvider BEFORE this call and it will survive because
        // we only remove AnthropicProvider-shaped registrations.
        var existing = services
            .Where(d => d.ServiceType == typeof(IAiProvider) ||
                        (d.ImplementationType == typeof(AnthropicProvider)))
            .ToList();
        foreach (var descriptor in existing)
        {
            services.Remove(descriptor);
        }

        services.AddHttpClient<IAiProvider, OllamaProvider>();

        // Health-check: hosted service that probes /api/tags on startup. The
        // HttpClient is resolved from IHttpClientFactory (registered via
        // AddHttpClient above) rather than typed-client injection so we can keep
        // the health check a singleton without coupling it to DefaultHttpClientFactory
        // lifetime quirks.
        services.AddHttpClient(nameof(OllamaHealthCheck));
        services.AddHostedService(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = factory.CreateClient(nameof(OllamaHealthCheck));
            return new OllamaHealthCheck(
                httpClient,
                sp.GetRequiredService<IOptions<OllamaOptions>>(),
                sp.GetRequiredService<ILogger<OllamaHealthCheck>>());
        });

        return services;
    }

}