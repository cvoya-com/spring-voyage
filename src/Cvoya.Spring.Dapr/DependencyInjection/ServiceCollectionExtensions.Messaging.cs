// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Lifecycle;
using Cvoya.Spring.Dapr.Messaging;
using Cvoya.Spring.Dapr.Prompts;
using Cvoya.Spring.Dapr.Units;
using Cvoya.Spring.Dapr.Workflows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Messaging-adjacent registrations: message-delivery seam, messaging tool
/// handlers, unit execution, validation, and prompt context builders.
/// </summary>
internal static class ServiceCollectionExtensionsMessaging
{
    internal static IServiceCollection AddCvoyaSpringMessaging(
        this IServiceCollection services,
        SpringHostRole role)
    {
        // #601 / #603 / #409 B-wide: read/write seam for the persisted unit
        // execution block. Shared between UnitCreationService (manifest
        // apply) and the dedicated `/api/v1/units/{id}/execution` HTTP
        // surface so the two write paths cannot drift on shape or
        // validation. TryAdd so a hosted overlay can swap in a
        // tenant-scoped variant.
        services.TryAddSingleton<IUnitExecutionStore, DbUnitExecutionStore>();

        // #947 / T-05: scheduler for ArtefactValidationWorkflow. Called by
        // UnitActor whenever it enters Validating so the workflow can run
        // the in-container probe. TryAdd so the private cloud repo can
        // override with a tenant-routing scheduler (e.g. per-tenant Dapr
        // app ids) without forking the OSS default.
        services.TryAddSingleton<IArtefactValidationWorkflowScheduler, ArtefactValidationWorkflowScheduler>();

        // #947 / T-05: per-unit validation tracker — writes
        // LastValidationRunId / LastValidationErrorJson onto
        // UnitDefinitionEntity. Separate from the other Definition-JSON
        // stores because the columns are dedicated and writes are
        // single-field updates. TryAdd keeps the cloud-overlay hook open.
        services.TryAddSingleton<IArtefactValidationTracker, DbArtefactValidationTracker>();

        // #1280: validation-scheduling collaborator extracted from UnitActor.
        // Owns the scheduling trigger, run-id persistence, and terminal-
        // callback logic that used to live inline in the actor. TryAdd so the
        // cloud overlay can substitute a tenant-aware coordinator (e.g. one
        // that routes workflows to per-tenant Dapr app ids or adds audit
        // logging) without touching the actor.
        services.TryAddSingleton<IArtefactValidationCoordinator, ArtefactValidationCoordinator>();
        services.TryAddSingleton<IUnitMembershipCoordinator, UnitMembershipCoordinator>();

        // #2049 / ADR-0040: unit live-config (model, color, provider,
        // hosting), boundary, permission-inheritance flag, and
        // own-expertise live in EF, not actor state. The singleton
        // store wraps the scoped EF repository so UnitActor (not
        // request-scoped) can read / write through it. TryAddSingleton
        // so the cloud overlay can layer audit / cross-tenant guards
        // on top.
        services.TryAddSingleton<IUnitLiveConfigStore, UnitLiveConfigStore>();

        // #2049 / ADR-0040: unit metadata / boundary / permission-
        // inheritance / own-expertise CRUD seam. Singleton: stateless
        // across units; the metadata / boundary / inheritance /
        // expertise reads + writes flow through IUnitLiveConfigStore.
        // TryAdd so the private cloud repo can substitute a
        // tenant-aware implementation without touching this
        // registration. Replaces the pre-#2049 IUnitPermissionCoordinator
        // (which only owned the inheritance flag) with the unified
        // EF-backed surface.
        services.TryAddSingleton<IUnitStateCoordinator, UnitStateCoordinator>();

        // #601 B-wide: companion read/write seam for the agent's own
        // execution block on AgentDefinitions.Definition. Shared between
        // manifest apply and the dedicated /api/v1/agents/{id}/execution
        // HTTP surface.
        services.TryAddSingleton<IAgentExecutionStore, DbAgentExecutionStore>();

        // ADR-0049 / #2576: the shared message-delivery seam (validation,
        // per-thread hop budget, bounded-retry delivery) and the messaging
        // tool handlers built on top of it. Singletons: stateless across
        // calls; per-call state flows through method arguments. ADR-0051
        // re-fronts the handlers as SvMessagingSkillRegistry — registered
        // in AddCvoyaSpringDaprRouting alongside the other Sv*SkillRegistry
        // types — so sv.messaging.* is served by the single platform MCP
        // server under the MCP session token.
        services.TryAddSingleton<MessageDeliveryService>();
        services.TryAddSingleton<MessagingToolHandlers>();

        // ADR-0062 § 3: TenantUser → routable `human://` sender resolution
        // at the API boundary. Scoped because the implementation holds a
        // SpringDbContext. OSS and cloud share the same implementation —
        // both walk the FK on `humans.tenant_user_id` introduced by the
        // same ADR. TryAdd keeps the override seam open for cloud
        // decorators (e.g. permission-checked variants).
        services.TryAddScoped<ITenantUserHumanResolver, TenantUserHumanResolver>();

        // ADR-0039 §3 gate 6 — cross-tenant containment. The OSS overlay
        // ships single-tenant; every address resolves to OssTenantIds.Default,
        // so the gate is a structural impossibility to violate. The cloud
        // overlay registers a tenant-aware resolver that consults the
        // persisted entity rows to reject foreign-tenant calls.
        services.TryAddSingleton<IMessageTenantResolver, SingleTenantMessageTenantResolver>();

        // ADR-0039 C1: runtime-invocation pipeline extracted from
        // AgentActor's activate-and-dispatch closure. Singleton: stateless
        // across subjects; per-call delegates carry per-actor state. C2
        // wires UnitActor through the same path, replacing the strategy
        // resolver. TryAdd so the cloud overlay can substitute a
        // tenant-aware variant (e.g. one that layers cost attribution or
        // tenant-scoped tool resolution) without touching this registration.
        // ADR-0052 / #2618: the runtime-invocation pipeline is consumed only
        // by AgentActor / UnitActor and transitively depends on the
        // execution-host-only IExecutionDispatcher, so it registers on the
        // execution host (spring-worker) only. The HTTP front door hosts no
        // actors and never resolves it.
        if (role == SpringHostRole.ExecutionHost)
        {
            services.TryAddSingleton<Cvoya.Spring.Dapr.Actors.IRuntimeInvocationPath,
                Cvoya.Spring.Dapr.Actors.RuntimeInvocationPath>();
        }

        // Prompt
        services.AddSingleton<UnitContextBuilder>();
        services.AddSingleton<AgentInstructionsBuilder>();

        return services;
    }
}
