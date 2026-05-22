// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IArtefactAutoStartGate"/>. Holds no per-artefact
/// state — the per-kind execution-store / credential-resolver lookups happen
/// inside a fresh DI scope on every call (matches the
/// <c>ArtefactValidationWorkflowScheduler</c> pattern).
/// </summary>
public class ArtefactAutoStartGate(
    IServiceScopeFactory scopeFactory,
    IActorProxyFactory actorProxyFactory,
    ILoggerFactory loggerFactory) : IArtefactAutoStartGate
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ArtefactAutoStartGate>();

    /// <inheritdoc />
    public async Task<LifecycleStatus> TryAutoStartAsync(
        ArtefactKind kind,
        Guid actorGuid,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        if (kind is not (ArtefactKind.Unit or ArtefactKind.Agent))
        {
            // Skill / HumanTemplate have no container lifecycle; the gate
            // is a no-op for them.
            return LifecycleStatus.Draft;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var runtimeCatalog = scope.ServiceProvider.GetService<IRuntimeCatalog>();
        var credentialResolver = scope.ServiceProvider.GetService<ILlmCredentialResolver>();

        // Resolve the per-kind execution defaults into a uniform shape so the
        // rest of the gate can run kind-agnostic. A missing store ⇒ Draft,
        // matching the legacy fail-safe (gate stays closed in test harnesses
        // that don't register the store).
        string? image;
        string? runtimeId;
        Cvoya.Spring.Core.Catalog.Model? model;
        switch (kind)
        {
            case ArtefactKind.Unit:
                {
                    var store = scope.ServiceProvider.GetService<IUnitExecutionStore>();
                    if (store is null) return LifecycleStatus.Draft;

                    UnitExecutionDefaults? defaults;
                    try
                    {
                        defaults = await store.GetAsync(GuidFormatter.Format(actorGuid), cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex,
                            "Unit '{DisplayName}' auto-start gate: failed to read execution defaults; leaving in Draft.",
                            displayName);
                        return LifecycleStatus.Draft;
                    }

                    if (defaults is null) return LifecycleStatus.Draft;
                    image = defaults.Image;
                    model = defaults.Model;
                    runtimeId = defaults.Runtime;
                    break;
                }
            case ArtefactKind.Agent:
                {
                    var store = scope.ServiceProvider.GetService<IAgentExecutionStore>();
                    if (store is null) return LifecycleStatus.Draft;

                    AgentExecutionShape? shape;
                    try
                    {
                        shape = await store.GetAsync(GuidFormatter.Format(actorGuid), cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex,
                            "Agent '{DisplayName}' auto-start gate: failed to read execution defaults; leaving in Draft.",
                            displayName);
                        return LifecycleStatus.Draft;
                    }

                    if (shape is null) return LifecycleStatus.Draft;
                    image = shape.Image;
                    model = shape.Model;
                    runtimeId = shape.Runtime;
                    break;
                }
            default:
                return LifecycleStatus.Draft;
        }

        if (string.IsNullOrWhiteSpace(image) || model is null)
        {
            return LifecycleStatus.Draft;
        }
        if (string.IsNullOrWhiteSpace(runtimeId))
        {
            return LifecycleStatus.Draft;
        }

        // Without a runtime catalogue (legacy test harness) we cannot derive
        // the credential edge, so the gate stays closed — same fail-safe the
        // scheduler uses.
        if (runtimeCatalog is null) return LifecycleStatus.Draft;
        var catalogRuntime = runtimeCatalog.GetAgentRuntime(runtimeId);
        if (catalogRuntime is null || catalogRuntime.ModelProviders.Count == 0)
        {
            return LifecycleStatus.Draft;
        }

        // Credential check. Runtimes whose first edge declares no auth method
        // (e.g. Ollama) are auto-startable as soon as image+model+runtime are
        // present — the probe layer skips the credential step.
        var edge = catalogRuntime.ModelProviders[0];
        if (edge.AuthMethod is not null && credentialResolver is not null)
        {
            try
            {
                var resolution = await credentialResolver.ResolveAsync(
                    providerId: edge.Id,
                    authMethod: edge.AuthMethod.Value,
                    agentId: kind == ArtefactKind.Agent ? actorGuid : null,
                    unitId: kind == ArtefactKind.Unit ? actorGuid : null,
                    cancellationToken);
                if (string.IsNullOrEmpty(resolution.Value))
                {
                    return LifecycleStatus.Draft;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "{Kind} '{DisplayName}' auto-start gate: credential resolution threw; leaving in Draft.",
                    kind, displayName);
                return LifecycleStatus.Draft;
            }
        }

        // All preconditions met — drive the actor into Validating and arm the
        // post-validation auto-start. The flag is consumed once by
        // CompleteValidationAsync (#2156 / #2364), so a later manual
        // /revalidate still settles in Stopped.
        var actorId = GuidFormatter.Format(actorGuid);
        try
        {
            switch (kind)
            {
                case ArtefactKind.Unit:
                    {
                        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                            new ActorId(actorId), nameof(UnitActor));
                        var transition = await proxy.TransitionAsync(LifecycleStatus.Validating, cancellationToken);
                        if (transition is { Success: true })
                        {
                            await proxy.SetPendingAutoStartAsync(cancellationToken);
                            return LifecycleStatus.Validating;
                        }
                        _logger.LogWarning(
                            "Unit '{DisplayName}' failed to transition to Validating on creation: {Reason}. Staying in Draft.",
                            displayName, transition?.RejectionReason ?? "unknown");
                        return LifecycleStatus.Draft;
                    }
                case ArtefactKind.Agent:
                    {
                        var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
                            new ActorId(actorId), nameof(AgentActor));
                        var transition = await proxy.TransitionAsync(LifecycleStatus.Validating, cancellationToken);
                        if (transition is { Success: true })
                        {
                            await proxy.SetPendingAutoStartAsync(cancellationToken);
                            return LifecycleStatus.Validating;
                        }
                        _logger.LogWarning(
                            "Agent '{DisplayName}' failed to transition to Validating on creation: {Reason}. Staying in Draft.",
                            displayName, transition?.RejectionReason ?? "unknown");
                        return LifecycleStatus.Draft;
                    }
                default:
                    return LifecycleStatus.Draft;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Kind} '{DisplayName}' transition to Validating threw on creation. Staying in Draft.",
                kind, displayName);
            return LifecycleStatus.Draft;
        }
    }
}
