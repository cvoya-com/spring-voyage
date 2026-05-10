// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Dapr.Cloning;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Initiative;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Initiative and policy registrations: cancellation manager, policy stores,
/// budget tracker, initiative engine, cognition providers, cloning policies,
/// and reflection-action handlers.
/// </summary>
internal static class ServiceCollectionExtensionsInitiative
{
    internal static IServiceCollection AddCvoyaSpringInitiative(
        this IServiceCollection services)
    {
        // Initiative — use TryAdd so the private repo can override any implementation.
        // The Dapr state-store-backed variants are registered as defaults so policies and
        // budget state survive process restarts and are shared across replicas; the
        // in-memory implementations remain available for tests that register them first.
        services.TryAddSingleton<ICancellationManager, CancellationManager>();
        services.TryAddSingleton<IAgentPolicyStore, DaprStateAgentPolicyStore>();
        services.TryAddSingleton<IInitiativeBudgetTracker, DaprStateInitiativeBudgetTracker>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IInitiativeEngine, InitiativeEngine>();

        // Agent-scoped initiative evaluator (#415 / PR-PLAT-INIT-1). Scoped
        // because the default implementation depends on IUnitPolicyEnforcer,
        // which in turn pulls in the scoped unit-membership repository.
        // TryAdd so the private cloud repo can layer a tenant-aware / audit
        // decorator without touching this registration.
        services.TryAddScoped<IAgentInitiativeEvaluator, DefaultAgentInitiativeEvaluator>();

        // Persistent cloning policy (#416 / #2051 / ADR-0040). EF-backed:
        // policies live on the tenant-scoped cloning_policies table.
        // Scoped registration aligns with SpringDbContext (per-request) so
        // tenant filters resolve from the ambient ITenantContext at query
        // time. The default enforcer is scoped because it composes the
        // scoped unit-membership repository — the private cloud repo can
        // layer a tenant-aware decorator via TryAdd without reshaping
        // persistence. The endpoint resolves IAgentCloningPolicyEnforcer
        // from the scoped request container per call.
        services.TryAddScoped<IAgentCloningPolicyRepository, EfAgentCloningPolicyRepository>();
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

        return services;
    }
}
