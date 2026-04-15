// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

using Cvoya.Spring.Core.Capabilities;
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
        // Dapr client, actor proxy factory, and workflow client
        services.AddDaprClient();
        services.TryAddSingleton<IActorProxyFactory>(_ => new ActorProxyFactory());
        services.AddDaprWorkflow(options => { });

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
                if (IsDesignTimeTooling())
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
                    options.UseNpgsql(connectionString));
            }
        }

        // Database startup: apply pending migrations on host start by
        // default. Operators running migrations out-of-band can set
        // Database:AutoMigrate=false.
        services.AddOptions<DatabaseOptions>().BindConfiguration(DatabaseOptions.SectionName);
        services.AddHostedService<DatabaseMigrator>();

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

        // Unit-membership backfill hosted service (#160 / C2b-1).
        // Gated by Database:BackfillMemberships; idempotent; short-lived.
        services.AddHostedService<UnitMembershipBackfillService>();

        // Options
        services.AddOptions<AiProviderOptions>().BindConfiguration(AiProviderOptions.SectionName);
        services.AddOptions<ContainerRuntimeOptions>().BindConfiguration("ContainerRuntime");
        services.AddOptions<UnitRuntimeOptions>().BindConfiguration(UnitRuntimeOptions.SectionName);
        services.AddOptions<WorkflowOrchestrationOptions>().BindConfiguration("WorkflowOrchestration");

        // Routing
        services.AddSingleton<DirectoryCache>();
        services.AddSingleton<IDirectoryService, DirectoryService>();
        services.TryAddSingleton<IAgentProxyResolver, AgentProxyResolver>();
        services.TryAddSingleton<MessageRouter>();
        services.TryAddSingleton<IMessageRouter>(sp => sp.GetRequiredService<MessageRouter>());

        // Execution — AnthropicProvider needs HttpClient
        services.AddHttpClient<IAiProvider, AnthropicProvider>();
        services.AddSingleton<IPromptAssembler, PromptAssembler>();
        services.AddSingleton<IPlatformPromptProvider, PlatformPromptProvider>();
        services.AddSingleton<IContainerRuntime, PodmanRuntime>();
        services.AddSingleton<IDaprSidecarManager, DaprSidecarManager>();
        services.AddSingleton<ContainerLifecycleManager>();
        services.TryAddSingleton<IUnitContainerLifecycle, UnitContainerLifecycle>();
        services.AddSingleton<IExecutionDispatcher, DelegatedExecutionDispatcher>();

        // Agent definition + tool launchers used by DelegatedExecutionDispatcher.
        services.TryAddSingleton<IAgentDefinitionProvider, DbAgentDefinitionProvider>();
        services.AddSingleton<IAgentToolLauncher, ClaudeCodeLauncher>();

        // In-process MCP server (hosted service — started automatically by the host).
        services.AddOptions<McpServerOptions>().BindConfiguration(McpServerOptions.SectionName);
        services.TryAddSingleton<McpServer>();
        services.TryAddSingleton<IMcpServer>(sp => sp.GetRequiredService<McpServer>());
        services.AddHostedService(sp => sp.GetRequiredService<McpServer>());

        // Initiative — use TryAdd so the private repo can override any implementation.
        // The Dapr state-store-backed variants are registered as defaults so policies and
        // budget state survive process restarts and are shared across replicas; the
        // in-memory implementations remain available for tests that register them first.
        services.TryAddSingleton<ICancellationManager, CancellationManager>();
        services.TryAddSingleton<IAgentPolicyStore, DaprStateAgentPolicyStore>();
        services.TryAddSingleton<IInitiativeBudgetTracker, DaprStateInitiativeBudgetTracker>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IInitiativeEngine, InitiativeEngine>();

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

        // Unkeyed default: UnitActor (activated by the Dapr runtime via DI) takes an
        // unkeyed IOrchestrationStrategy — provide one so construction succeeds.
        // "ai" is the platform default; private-cloud code can pre-register a
        // different default via TryAdd.
        services.TryAddSingleton<IOrchestrationStrategy>(
            sp => sp.GetRequiredKeyedService<IOrchestrationStrategy>("ai"));


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
        services.AddHostedService<ActivityEventPersister>();
        services.AddOptions<StreamEventPublisherOptions>().BindConfiguration(StreamEventPublisherOptions.SectionName);
        services.AddSingleton<StreamEventPublisher>();
        services.AddSingleton<StreamEventSubscriber>();

        // Auth
        services.AddSingleton<IPermissionService, PermissionService>();

        // Costs
        services.AddHostedService<CostTracker>();
        services.AddScoped<ICostQueryService, CostAggregation>();
        services.AddHostedService<BudgetEnforcer>();
        services.AddScoped<ICostTracker, CloneCostTracker>();

        // Observability — query service
        services.AddScoped<IActivityQueryService, ActivityQueryService>();

        return services;
    }

    /// <summary>
    /// Detects whether the current process was launched by a design-time
    /// tool that loads the host assembly but never opens the database —
    /// for example <c>dotnet-ef</c> running migrations and
    /// <c>GetDocument.Insider</c> emitting the build-time OpenAPI document.
    /// These tools rely on the DI container building successfully without a
    /// real connection string; production paths must not.
    /// </summary>
    private static bool IsDesignTimeTooling()
    {
        var entryName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        if (entryName is null)
        {
            return false;
        }
        return entryName is "GetDocument.Insider"
            or "dotnet-getdocument"
            or "ef"
            or "dotnet-ef";
    }
}