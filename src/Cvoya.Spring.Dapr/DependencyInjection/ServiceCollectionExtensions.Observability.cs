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

        // #2044 / ADR-0040: ACL writes go to EF, not actor state. The
        // store is the scope-creating wrapper around the scoped EF
        // repository — UnitActor (singleton-style) writes through it,
        // and PermissionService reads through it. TryAddSingleton so a
        // cloud overlay can decorate with audit logging or cross-tenant
        // guards without forking the OSS default.
        services.TryAddSingleton<IUnitHumanPermissionStore, UnitHumanPermissionStore>();
        services.TryAddSingleton<IPermissionService, PermissionService>();

        // Costs — scoped query/tracking services always registered for endpoint DI.
        services.AddScoped<ICostQueryService, CostAggregation>();
        services.AddScoped<ICostTracker, CloneCostTracker>();

        // Observability — query services
        services.AddScoped<IActivityQueryService, ActivityQueryService>();
        // Analytics rollups (#457). TryAdd so the private cloud repo can
        // decorate with tenant-scoped filters without forking the OSS default.
        services.TryAddScoped<IAnalyticsQueryService, AnalyticsQueryService>();

        // Thread projection (#452 / #456 / #2054). Reads from the
        // EF-authoritative `threads` and `messages` tables (ADR-0030 /
        // ADR-0040); the legacy activity-event JSON scan is gone. TryAdd so
        // the private cloud host can swap in a tenant-scoped implementation
        // without touching the endpoints.
        services.TryAddScoped<IThreadQueryService, ThreadQueryService>();

        // Inbox identity resolver (#2766 / ADR-0062 § 7). Maps the calling
        // TenantUser to the set of HumanEntity ids the inbox query should
        // match against by walking the FK on humans.tenant_user_id. OSS
        // and cloud share the implementation now that the explicit FK
        // landed; the cloud overlay's separate registration goes away.
        services.TryAddScoped<IInboxIdentityResolver, InboxIdentityResolver>();

        // Single-message lookup (#1209 / #2054). Backs
        // `GET /api/v1/messages/{id}` and `spring message show <id>`. Reads
        // from the `messages` table directly; cloud overlays can swap the
        // implementation through DI without touching call sites.
        services.TryAddScoped<IMessageQueryService, MessageQueryService>();

        // Activity-capture (#2492): tenant settings + OTLP ingest +
        // retention sweep. TryAdd so the private cloud overlay can swap
        // in a decorated implementation (e.g. one that forwards to an
        // external OTel backend) without touching the OSS endpoint.
        services.TryAddScoped<ITenantActivitySettings, TenantActivitySettingsService>();
        // OtlpIngestService keeps an in-process token-bucket per
        // (subject, kind) so its rate-limiting state persists across
        // requests; it resolves the scoped ITenantActivitySettings
        // through a per-batch DI scope.
        //
        // #2503: the forwarding decorator wraps the inner ingest. We
        // register the inner concretely and resolve the IOtlpIngestService
        // contract via a factory that builds the decorator over the
        // inner. The decorator's status table is read by the
        // platform-operator surfaces — register it concretely too so
        // those endpoints can resolve the singleton.
        services.TryAddSingleton<OtlpIngestService>();
        services.AddHttpClient(ForwardingOtlpIngestServiceDecorator.HttpClientName);
        services.TryAddSingleton<ForwardingOtlpIngestServiceDecorator>(sp => new ForwardingOtlpIngestServiceDecorator(
            inner: sp.GetRequiredService<OtlpIngestService>(),
            scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
            httpClientFactory: sp.GetRequiredService<IHttpClientFactory>(),
            timeProvider: sp.GetRequiredService<TimeProvider>(),
            logger: sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ForwardingOtlpIngestServiceDecorator>>()));
        services.TryAddSingleton<IOtlpIngestService>(sp => sp.GetRequiredService<ForwardingOtlpIngestServiceDecorator>());

        // Hosted services that depend on runtime infrastructure (Dapr state store,
        // database). During build-time OpenAPI generation none of this is
        // available, so skip registration to avoid noisy startup errors. See #370.
        if (!isDocGen)
        {
            services.AddHostedService<ActivityEventPersister>();
            services.AddHostedService<CostTracker>();
            services.AddHostedService<BudgetEnforcer>();
            services.AddHostedService<ActivityRetentionPurgeService>();
        }

        return services;
    }
}
