// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.CredentialHealth;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Connectors;
using Cvoya.Spring.Dapr.CredentialHealth;
using Cvoya.Spring.Dapr.ModelProviders;
using Cvoya.Spring.Dapr.Skills;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

/// <summary>
/// Tenant plugin registrations: skill bundles (resolver, validator,
/// seeder), model-provider and connector install services, tenant
/// registry, and credential-health store.
/// </summary>
internal static class ServiceCollectionExtensionsTenantPlugins
{
    internal static IServiceCollection AddCvoyaSpringTenantPlugins(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Skill bundles (#167 / C4). The resolver is a singleton — it reads
        // from disk and caches per-host. The validator is scoped because it
        // depends on IUnitPolicyRepository (which is scoped). TryAdd so the
        // cloud host can register a tenant-scoped bundle store or validator
        // without touching the API layer.
        services.AddOptions<SkillBundleOptions>().BindConfiguration(SkillBundleOptions.SectionName);
        // Fall back to the shared `Packages:Root` (or `SPRING_PACKAGES_ROOT`
        // env) when `Skills:PackagesRoot` is unset, so one deployment-level
        // config key serves both the unit-template catalog and the skill-
        // bundle resolver/seeder. Without this, the default-tenant bootstrap
        // (which the Worker owns; see WorkerComposition) sees
        // SkillBundleOptions.PackagesRoot as null and silently skips
        // enumeration — leaving the tenant with zero bindings so every
        // template-backed Create hits "Unknown skill package". See #969.
        services.AddSingleton<IPostConfigureOptions<SkillBundleOptions>>(
            new SkillBundlePackagesRootFallback(configuration));
        // #687: resolve `ISkillBundleResolver` through a tenant-filtering
        // decorator so bundles surface only when the current tenant has an
        // `enabled=true` binding. The inner file-system resolver stays a
        // singleton (its cache is restart-scoped); the decorator is scoped
        // because the binding service holds a SpringDbContext.
        services.TryAddSingleton<FileSystemSkillBundleResolver>();
        services.TryAddScoped<ISkillBundleResolver, TenantFilteringSkillBundleResolver>();
        services.TryAddScoped<ITenantSkillBundleBindingService, DefaultTenantSkillBundleBindingService>();
        services.TryAddScoped<ISkillBundleValidator, DefaultSkillBundleValidator>();
        // The bundle stores depend on the scoped ISkillBundleResolver
        // (TenantFilteringSkillBundleResolver), so they must themselves be
        // scoped — the underlying IStateStore is a Dapr-backed singleton
        // wrapper so concurrent scoped instances share a single backing
        // state-store handle. The per-subject Unit/Agent key prefixes
        // namespace the JSON docs in the shared store.
        services.TryAddScoped<IUnitSkillBundleStore, StateStoreBackedUnitSkillBundleStore>();
        services.TryAddScoped<IAgentSkillBundleStore, StateStoreBackedAgentSkillBundleStore>();

        // Default-tenant bootstrap seed adapter for the file-system bundle
        // resolver (#676). Registered as an enumerable ITenantSeedProvider
        // so the DefaultTenantBootstrapService picks it up on first run;
        // the wrapper is a thin enumeration that keeps the resolver in
        // the Phase 1 bootstrap loop without coupling it to an OSS bundle
        // install table that does not yet exist (Phase 2 follow-up).
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITenantSeedProvider, FileSystemSkillBundleSeedProvider>());

        // Per-tenant model-provider + connector install services (#683,
        // #684). Scoped because they depend on SpringDbContext; paired
        // with singleton seed providers that crack open a child DI scope
        // per seed pass. TryAdd* so a cloud overlay can register a
        // tenant-scoped variant (e.g. backed by a different repository)
        // without touching this call site.
        services.TryAddScoped<ITenantModelProviderInstallService, TenantModelProviderInstallService>();
        services.TryAddScoped<ITenantConnectorInstallService, TenantConnectorInstallService>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITenantSeedProvider, ModelProviderInstallSeedProvider>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITenantSeedProvider, ConnectorInstallSeedProvider>());

        // Platform-tenant registry (#1260 / C1.2d). Scoped because it
        // depends on SpringDbContext. The endpoints that consume this
        // surface are gated to PlatformOperator at the API layer; the
        // cloud overlay can register a permission-checked variant ahead
        // of this TryAdd* call.
        services.TryAddScoped<ITenantRegistry, TenantRegistry>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITenantSeedProvider, DefaultTenantRecordSeedProvider>());

        // Seed the OSS operator TenantUser pinned at OssTenantUserIds.Operator
        // (#2768). Priority slot 7 runs the row in after the tenant-record
        // seeder (priority 5) and before the plugin seeders (10 / 20) so any
        // downstream seeder that wants to declare a soft FK to
        // tenant_users.id has the row available.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITenantSeedProvider, DefaultTenantUserSeedProvider>());

        // Credential-health store (#686). Scoped because it holds a
        // SpringDbContext. The DelegatingHandler that feeds this store at
        // use-time opens a child DI scope per write so it can be invoked
        // from any HttpClient pipeline, regardless of ambient scope.
        services.TryAddScoped<ICredentialHealthStore, DefaultCredentialHealthStore>();

        return services;
    }

    /// <summary>
    /// Post-configure that bridges <see cref="SkillBundleOptions.PackagesRoot"/>
    /// to the shared <c>Packages:Root</c> configuration key (or the
    /// <c>SPRING_PACKAGES_ROOT</c> environment variable) when the operator
    /// hasn't set <c>Skills:PackagesRoot</c> explicitly. Registered by
    /// <see cref="AddCvoyaSpringDapr"/> so both the API host and the
    /// Worker host (which owns the default-tenant bootstrap) agree on the
    /// packages root without either having to know about the other's DI
    /// graph. See #969.
    /// </summary>
    private sealed class SkillBundlePackagesRootFallback(IConfiguration configuration)
        : IPostConfigureOptions<SkillBundleOptions>
    {
        public void PostConfigure(string? name, SkillBundleOptions options)
        {
            if (!string.IsNullOrWhiteSpace(options.PackagesRoot))
            {
                return;
            }

            options.PackagesRoot = configuration["Packages:Root"]
                ?? System.Environment.GetEnvironmentVariable("SPRING_PACKAGES_ROOT");
        }
    }
}
