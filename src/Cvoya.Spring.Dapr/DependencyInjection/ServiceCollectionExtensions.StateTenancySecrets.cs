// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Secrets;
using Cvoya.Spring.Dapr.State;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// State, tenancy, and secrets registrations: Dapr state store, tenant
/// context, scope bypass, secret store, secret registry/resolver, and
/// secret access policy.
/// </summary>
internal static class ServiceCollectionExtensionsStateTenancySecrets
{
    internal static IServiceCollection AddCvoyaSpringStateTenancySecrets(
        this IServiceCollection services)
    {
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
        services.AddOptions<TenancyOptions>().BindConfiguration(TenancyOptions.SectionName);
        services.TryAddSingleton<ITenantContext, ConfiguredTenantContext>();
        // ADR-0062 § 1: deployment-default Human → TenantUser binding.
        // OSS returns the operator literal; cloud overlays register a
        // scoped resolver that returns the calling principal. TryAdd so
        // the overlay's pre-registration wins.
        services.TryAddSingleton<ITenantUserDefaultResolver, OssTenantUserDefaultResolver>();
        // Cross-tenant bypass helper (#677). AsyncLocal-backed nesting-safe
        // scope with structured audit logging on open / close — the
        // EF query filters introduced in the #675 sibling PR consult its
        // IsBypassActive flag for legitimate system-wide reads
        // (DatabaseMigrator, platform analytics). TryAdd so the private
        // cloud repo can swap in a permission-checked variant (e.g. one
        // that requires a platform-admin grant on the caller principal)
        // without touching any call site.
        services.TryAddSingleton<ITenantScopeBypass, TenantScopeBypass>();
        services.TryAddSingleton<ISecretsEncryptor, SecretsEncryptor>();
        services.TryAddSingleton<ISecretStore, DaprStateBackedSecretStore>();
        services.TryAddScoped<ISecretRegistry, EfSecretRegistry>();
        services.TryAddScoped<ISecretResolver, ComposedSecretResolver>();
        // ISecretAccessPolicy: OSS default authorizes everything. The
        // private cloud repo replaces this with a tenant-admin / platform-admin
        // check driven by the authenticated principal — the endpoints only
        // depend on the interface, so no endpoint code has to change.
        services.TryAddSingleton<ISecretAccessPolicy, AllowAllSecretAccessPolicy>();

        return services;
    }
}
