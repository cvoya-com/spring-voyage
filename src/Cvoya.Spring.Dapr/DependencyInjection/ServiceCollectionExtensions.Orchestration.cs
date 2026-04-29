// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Configuration;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Orchestration;
using Cvoya.Spring.Dapr.Prompts;
using Cvoya.Spring.Dapr.Units;
using Cvoya.Spring.Dapr.Workflows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Orchestration registrations: orchestration strategies, strategy providers,
/// resolvers, unit orchestration + execution stores, validation, and prompt
/// context builders.
/// </summary>
internal static class ServiceCollectionExtensionsOrchestration
{
    internal static IServiceCollection AddCvoyaSpringOrchestration(
        this IServiceCollection services)
    {
        services.AddOptions<WorkflowOrchestrationOptions>().BindConfiguration("WorkflowOrchestration");

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
        // IOrchestrationStrategyResolver.
        //
        // The inner DB provider is registered as a concrete singleton; the
        // public IOrchestrationStrategyProvider is a caching decorator (#518)
        // wrapping it. Keying off the strategy slot is hot path — every
        // domain message to a unit asks for it — but the slot only changes
        // when a manifest is re-applied. Without the decorator each message
        // opens an AsyncServiceScope and issues a FirstOrDefaultAsync against
        // Postgres; with it, steady-state traffic for a single unit
        // resolves from memory. The decorator combines a short TTL
        // (self-healing after cross-process writes) with an explicit
        // invalidation hook (immediate consistency after in-process writes).
        //
        // All three registrations are TryAdd'd so the private cloud host can
        // pre-register a tenant-scoped reader, a different cache shape, or a
        // no-op invalidator without forking. The concrete DB provider stays
        // resolvable by its concrete type for cache-off testing.
        services.TryAddSingleton<DbOrchestrationStrategyProvider>();
        services.TryAddSingleton<CachingOrchestrationStrategyProvider>(sp =>
            new CachingOrchestrationStrategyProvider(
                sp.GetRequiredService<DbOrchestrationStrategyProvider>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));
        services.TryAddSingleton<IOrchestrationStrategyProvider>(sp =>
            sp.GetRequiredService<CachingOrchestrationStrategyProvider>());
        services.TryAddSingleton<IOrchestrationStrategyCacheInvalidator>(sp =>
            sp.GetRequiredService<CachingOrchestrationStrategyProvider>());
        services.TryAddSingleton<IOrchestrationStrategyResolver, DefaultOrchestrationStrategyResolver>();

        // #606: read/write seam for the persisted orchestration.strategy
        // slot. Shared between UnitCreationService (manifest apply) and the
        // dedicated `/api/v1/units/{id}/orchestration` HTTP surface so the
        // two write paths cannot drift. TryAdd so the private cloud repo
        // can swap in a tenant-scoped reader before calling the OSS
        // extension.
        services.TryAddSingleton<IUnitOrchestrationStore, DbUnitOrchestrationStore>();

        // #601 / #603 / #409 B-wide: read/write seam for the persisted unit
        // execution block. Shared between UnitCreationService (manifest
        // apply) and the dedicated `/api/v1/units/{id}/execution` HTTP
        // surface so the two write paths cannot drift on shape or
        // validation. TryAdd so a hosted overlay can swap in a
        // tenant-scoped variant.
        services.TryAddSingleton<IUnitExecutionStore, DbUnitExecutionStore>();

        // #947 / T-05: scheduler for UnitValidationWorkflow. Called by
        // UnitActor whenever it enters Validating so the workflow can run
        // the in-container probe. TryAdd so the private cloud repo can
        // override with a tenant-routing scheduler (e.g. per-tenant Dapr
        // app ids) without forking the OSS default.
        services.TryAddSingleton<IUnitValidationWorkflowScheduler, UnitValidationWorkflowScheduler>();

        // #947 / T-05: per-unit validation tracker — writes
        // LastValidationRunId / LastValidationErrorJson onto
        // UnitDefinitionEntity. Separate from the other Definition-JSON
        // stores because the columns are dedicated and writes are
        // single-field updates. TryAdd keeps the cloud-overlay hook open.
        services.TryAddSingleton<IUnitValidationTracker, DbUnitValidationTracker>();

        // #1280: validation-scheduling collaborator extracted from UnitActor.
        // Owns the scheduling trigger, run-id persistence, and terminal-
        // callback logic that used to live inline in the actor. TryAdd so the
        // cloud overlay can substitute a tenant-aware coordinator (e.g. one
        // that routes workflows to per-tenant Dapr app ids or adds audit
        // logging) without touching the actor.
        services.TryAddSingleton<IUnitValidationCoordinator, UnitValidationCoordinator>();
        services.TryAddSingleton<IUnitMembershipCoordinator, UnitMembershipCoordinator>();

        // #1311: permission-management collaborator extracted from UnitActor.
        // Owns the human-permission grant/revocation logic and the
        // UnitPermissionInheritance flag management (ADR-0013). TryAdd so the
        // cloud overlay can substitute a tenant-aware coordinator (e.g. one
        // that enforces cross-tenant permission guards or adds audit logging on
        // every grant / revocation) without touching the actor.
        services.TryAddSingleton<IUnitPermissionCoordinator, UnitPermissionCoordinator>();

        // #601 B-wide: companion read/write seam for the agent's own
        // execution block on AgentDefinitions.Definition. Shared between
        // manifest apply and the dedicated /api/v1/agents/{id}/execution
        // HTTP surface.
        services.TryAddSingleton<IAgentExecutionStore, DbAgentExecutionStore>();

        // Prompt
        services.AddSingleton<UnitContextBuilder>();
        services.AddSingleton<ThreadContextBuilder>();

        return services;
    }
}