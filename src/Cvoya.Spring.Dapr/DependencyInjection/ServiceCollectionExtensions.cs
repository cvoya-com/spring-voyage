// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Costs;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Observability;
using Cvoya.Spring.Dapr.Orchestration;
using Cvoya.Spring.Dapr.Prompts;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.State;

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

        // EF Core / PostgreSQL
        var connectionString = configuration.GetConnectionString("SpringDb");
        services.AddDbContext<SpringDbContext>(options =>
        {
            if (!string.IsNullOrEmpty(connectionString))
            {
                options.UseNpgsql(connectionString);
            }
        });

        // Repositories
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));

        // Options
        services.AddOptions<AiProviderOptions>().BindConfiguration(AiProviderOptions.SectionName);
        services.AddOptions<ContainerRuntimeOptions>().BindConfiguration("ContainerRuntime");
        services.AddOptions<WorkflowOrchestrationOptions>().BindConfiguration("WorkflowOrchestration");

        // Routing
        services.AddSingleton<DirectoryCache>();
        services.AddSingleton<IDirectoryService, DirectoryService>();
        services.AddSingleton<MessageRouter>();

        // Execution — AnthropicProvider needs HttpClient
        services.AddHttpClient<IAiProvider, AnthropicProvider>();
        services.AddSingleton<IPromptAssembler, PromptAssembler>();
        services.AddSingleton<IPlatformPromptProvider, PlatformPromptProvider>();
        services.AddSingleton<IContainerRuntime, PodmanRuntime>();
        services.AddSingleton<IDaprSidecarManager, DaprSidecarManager>();
        services.AddSingleton<ContainerLifecycleManager>();
        services.AddKeyedSingleton<IExecutionDispatcher, HostedExecutionDispatcher>("hosted");
        services.AddKeyedSingleton<IExecutionDispatcher, DelegatedExecutionDispatcher>("delegated");

        // Orchestration
        services.AddKeyedSingleton<IOrchestrationStrategy, AiOrchestrationStrategy>("ai");
        services.AddKeyedSingleton<IOrchestrationStrategy, WorkflowOrchestrationStrategy>("workflow");


        // Prompt
        services.AddSingleton<UnitContextBuilder>();
        services.AddSingleton<ConversationContextBuilder>();

        // State
        services.AddOptions<DaprStateStoreOptions>().BindConfiguration(DaprStateStoreOptions.SectionName);
        services.AddSingleton<IStateStore, DaprStateStore>();

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

        // Activity bus
        services.AddSingleton<ActivityBus>();
        services.AddSingleton<IActivityObservable>(sp => sp.GetRequiredService<ActivityBus>());

        return services;
    }
}