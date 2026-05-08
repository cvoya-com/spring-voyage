// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Orchestration;
using Cvoya.Spring.Dapr.Prompts;
using Cvoya.Spring.Dapr.Units;
using Cvoya.Spring.Dapr.Workflows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Orchestration-adjacent registrations: tool providers, unit execution,
/// validation, and prompt context builders.
/// </summary>
internal static class ServiceCollectionExtensionsOrchestration
{
    internal static IServiceCollection AddCvoyaSpringOrchestration(
        this IServiceCollection services)
    {
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

        // ADR-0039 D2: default IOrchestrationToolProvider resolves the
        // directory-backed orchestration-tool surface for units with children.
        // TryAdd keeps the override hook open for hosts that need a decorated
        // or tenant-scoped provider.
        services.TryAddSingleton<IOrchestrationToolProvider, DirectoryOrchestrationToolProvider>();

        // ADR-0039 C1: runtime-invocation pipeline extracted from
        // AgentActor's activate-and-dispatch closure. Singleton: stateless
        // across subjects; per-call delegates carry per-actor state. C2
        // wires UnitActor through the same path, replacing the strategy
        // resolver. TryAdd so the cloud overlay can substitute a
        // tenant-aware variant (e.g. one that layers cost attribution or
        // tenant-scoped tool resolution) without touching this registration.
        services.TryAddSingleton<Cvoya.Spring.Dapr.Actors.IRuntimeInvocationPath,
            Cvoya.Spring.Dapr.Actors.RuntimeInvocationPath>();

        // Prompt
        services.AddSingleton<UnitContextBuilder>();
        services.AddSingleton<ThreadContextBuilder>();

        return services;
    }
}
