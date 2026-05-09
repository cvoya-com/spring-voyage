// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Configuration;
using Cvoya.Spring.Dapr.Costs;
using Cvoya.Spring.Dapr.Observability;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Observability, analytics, auth, and cost registrations: activity event
/// bus, stream publisher/subscriber, unit activity observable, hierarchy
/// resolver, permission service, cost services, and analytics query services.
/// </summary>
internal static class ServiceCollectionExtensionsObservability
{
    internal static IServiceCollection AddCvoyaSpringObservability(
        this IServiceCollection services)
    {
        var isDocGen = BuildEnvironment.IsDesignTimeTooling;

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

        // Thread projection (#452 / #456). Materialises threads
        // and inbox rows from the activity-event table — no separate message
        // store yet. TryAdd so the private cloud host can swap in a tenant-
        // scoped implementation without touching the endpoints.
        services.TryAddScoped<IThreadQueryService, ThreadQueryService>();

        // Single-message lookup (#1209). Backs `GET /api/v1/messages/{id}`
        // and `spring message show <id>`. Like the thread service
        // above this is a projection over the activity-event table; cloud
        // overlays can swap the implementation through DI without touching
        // call sites.
        services.TryAddScoped<IMessageQueryService, MessageQueryService>();

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
}
