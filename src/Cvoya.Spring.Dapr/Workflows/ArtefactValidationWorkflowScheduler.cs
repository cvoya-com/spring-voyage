// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Execution;

using global::Dapr.Workflow;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IArtefactValidationWorkflowScheduler"/>. For both
/// <see cref="ArtefactKind.Unit"/> and <see cref="ArtefactKind.Agent"/>
/// resolves the artefact's persisted execution defaults (<c>image</c>,
/// <c>agent</c>, <c>model</c>) and the LLM credential the chosen runtime
/// edge declares, then schedules a new <c>ArtefactValidationWorkflow</c> run
/// via <see cref="DaprWorkflowClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// Production DI registers this with <c>TryAddSingleton</c> so the private
/// cloud host can layer tenant-aware scheduling (e.g. per-tenant Dapr app
/// routing) without forking the OSS default.
/// </para>
/// <para>
/// The scheduler runs inside the Worker / API host and is the one place
/// that knows how to compose a <see cref="ArtefactValidationWorkflowInput"/>
/// from actor-side state. Keeping this resolution out of the actor lets the
/// actors stay pure Dapr-actor code: each actor emits an intent ("please
/// schedule validation for kind X actor id Y") and the scheduler does the
/// side-effectful plumbing on top of the shared DB and credential resolver.
/// </para>
/// </remarks>
public class ArtefactValidationWorkflowScheduler(
    DaprWorkflowClient workflowClient,
    IServiceScopeFactory scopeFactory,
    IRuntimeCatalog runtimeCatalog,
    IAgentRuntimeLauncherRegistry launcherRegistry,
    ILoggerFactory loggerFactory) : IArtefactValidationWorkflowScheduler
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ArtefactValidationWorkflowScheduler>();

    /// <inheritdoc />
    public async Task<ArtefactValidationSchedule> ScheduleAsync(
        ArtefactKind kind,
        string artefactActorId,
        CancellationToken cancellationToken = default)
    {
        if (kind is not (ArtefactKind.Unit or ArtefactKind.Agent))
        {
            throw new InvalidOperationException(
                $"ArtefactKind '{kind}' has no container lifecycle — only Unit and Agent are schedulable.");
        }

        if (string.IsNullOrWhiteSpace(artefactActorId))
        {
            throw new ArgumentException("Artefact actor id must be supplied.", nameof(artefactActorId));
        }
        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(artefactActorId, out var artefactActorUuid))
        {
            throw new ArgumentException(
                $"Artefact actor id '{artefactActorId}' is not a valid Guid.",
                nameof(artefactActorId));
        }

        // Both the SpringDbContext (per-request) and the
        // ILlmCredentialResolver (scoped) live behind a fresh DI scope —
        // the scheduler itself is a singleton so it cannot consume either
        // directly through its constructor.
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var credentialResolver = scope.ServiceProvider
            .GetRequiredService<ILlmCredentialResolver>();

        // Resolve the artefact's display name + execution defaults from the
        // right entity. The actor only knows its Dapr actor Guid; this query
        // is the cheapest join back to the user-facing label.
        string displayName;
        string? image;
        string? runtimeId;
        string? provider;
        string? model;
        switch (kind)
        {
            case ArtefactKind.Unit:
                {
                    var entity = await db.UnitDefinitions
                        .AsNoTracking()
                        .FirstOrDefaultAsync(
                            u => u.Id == artefactActorUuid && u.DeletedAt == null,
                            cancellationToken);
                    if (entity is null)
                    {
                        throw NotFoundException(kind, artefactActorId);
                    }

                    var defaults = DbUnitExecutionStore.Extract(entity.Definition);
                    if (defaults is null)
                    {
                        throw NoDefaultsException();
                    }

                    displayName = entity.DisplayName;
                    image = defaults.Image;
                    provider = defaults.Provider;
                    model = defaults.Model;
                    runtimeId = ResolveAgentRuntimeId(defaults.Agent, defaults.Provider);
                    break;
                }
            case ArtefactKind.Agent:
                {
                    var entity = await db.AgentDefinitions
                        .AsNoTracking()
                        .FirstOrDefaultAsync(
                            a => a.Id == artefactActorUuid && a.DeletedAt == null,
                            cancellationToken);
                    if (entity is null)
                    {
                        throw NotFoundException(kind, artefactActorId);
                    }

                    var shape = DbAgentExecutionStore.Extract(entity.Definition);
                    if (shape is null)
                    {
                        throw NoDefaultsException();
                    }

                    displayName = entity.DisplayName;
                    image = shape.Image;
                    provider = shape.Provider;
                    model = shape.Model;
                    runtimeId = ResolveAgentRuntimeId(shape.Agent, shape.Provider);
                    break;
                }
            default:
                throw new InvalidOperationException($"Unsupported kind {kind}.");
        }

        if (string.IsNullOrWhiteSpace(image))
        {
            throw new ArtefactValidationSchedulingException(new ArtefactValidationError(
                Step: ArtefactValidationStep.PullingImage,
                Code: ArtefactValidationCodes.ConfigurationIncomplete,
                Message: $"This {kind.ToString().ToLowerInvariant()} has no container image configured. " +
                    $"Set the image on the {kind} Execution tab and retry validation.",
                Details: new Dictionary<string, string>
                {
                    ["missing"] = "image",
                }));
        }

        if (string.IsNullOrWhiteSpace(runtimeId))
        {
            throw new ArtefactValidationSchedulingException(new ArtefactValidationError(
                Step: ArtefactValidationStep.VerifyingTool,
                Code: ArtefactValidationCodes.ConfigurationIncomplete,
                Message: $"This {kind.ToString().ToLowerInvariant()} has no runtime or provider configured. " +
                    "Pick a runtime (or provider) on the Execution tab and retry validation.",
                Details: new Dictionary<string, string>
                {
                    ["missing"] = "runtime",
                }));
        }

        // ADR-0038 (#1770): the resolver is keyed on (provider, authMethod).
        // The workflow scheduler resolves the credential against the
        // catalogue runtime entry's first edge — that's the auth method
        // the chosen launcher consumes. When the runtime declares no
        // credential (e.g. Ollama) the resolver returns NotFound and we
        // pass the empty string through.
        var providerForCredential = provider ?? runtimeId!;
        var authForCredential = AuthMethod.ApiKey;
        var catalogRuntime = runtimeCatalog.GetAgentRuntime(runtimeId!);
        if (catalogRuntime is { ModelProviders.Count: > 0 })
        {
            var edge = catalogRuntime.ModelProviders[0];
            providerForCredential = edge.Id;
            authForCredential = edge.AuthMethod ?? AuthMethod.ApiKey;
        }

        // Per-kind credential routing:
        //  Unit:  agentId=null,         unitId=artefactUuid (walks the parent-unit chain on miss)
        //  Agent: agentId=artefactUuid, unitId=null          (agent-scope first, then tenant fall-through)
        //
        // The agent path skips the parent-unit chain for v0.1 — most agents
        // inherit the tenant default and that path resolves cleanly. Agents
        // that need a unit-scoped override are a future enhancement (would
        // require resolving the agent's parent unit through
        // IUnitMembershipRepository before calling the resolver).
        var credentialResolution = await credentialResolver
            .ResolveAsync(
                providerId: providerForCredential,
                authMethod: authForCredential,
                agentId: kind == ArtefactKind.Agent ? artefactActorUuid : null,
                unitId: kind == ArtefactKind.Unit ? artefactActorUuid : null,
                cancellationToken);

        var credential = credentialResolution.Value ?? string.Empty;
        var requestedModel = model ?? string.Empty;

        // Determine which post-pull steps the runtime does not declare so
        // the workflow can skip them (emitting no events → UI shows "skipped").
        var skipSteps = ComputeSkipSteps(runtimeId!, requestedModel);

        var input = new ArtefactValidationWorkflowInput(
            Kind: kind,
            ArtefactId: artefactActorId,
            ArtefactName: displayName,
            Image: image!,
            RuntimeId: runtimeId!,
            Credential: credential,
            RequestedModel: requestedModel,
            SkipSteps: skipSteps);

        var instanceId = await workflowClient.ScheduleNewWorkflowAsync(
            nameof(ArtefactValidationWorkflow),
            input: input);

        _logger.LogInformation(
            "Scheduled ArtefactValidationWorkflow {InstanceId} for {Kind} {Name} (actor {ActorId}) image={Image} runtime={Runtime} model={Model}.",
            instanceId, kind, displayName, artefactActorId, image, runtimeId, requestedModel);

        return new ArtefactValidationSchedule(instanceId, displayName);
    }

    private static ArtefactValidationSchedulingException NotFoundException(ArtefactKind kind, string actorId)
        => new(new ArtefactValidationError(
            Step: ArtefactValidationStep.PullingImage,
            Code: ArtefactValidationCodes.ConfigurationIncomplete,
            Message: $"No {kind.ToString().ToLowerInvariant()} definition row exists for actor id '{actorId}'. " +
                $"The {kind.ToString().ToLowerInvariant()} may have been deleted; recreate it before validating.",
            Details: null));

    private static ArtefactValidationSchedulingException NoDefaultsException()
        => new(new ArtefactValidationError(
            Step: ArtefactValidationStep.PullingImage,
            Code: ArtefactValidationCodes.ConfigurationIncomplete,
            Message: "No execution defaults are configured. " +
                "Set a container image (and optionally a runtime) before validation can run.",
            Details: new Dictionary<string, string>
            {
                ["missing"] = "image,runtime",
            }));

    /// <summary>
    /// Resolves the agent-runtime registry id from the artefact's persisted
    /// execution defaults. Precedence — <c>agent</c> wins, then
    /// <c>provider</c>. <c>agent</c> is the source of truth (sourced from the
    /// manifest's <c>ai.runtime</c> field). <c>provider</c> is a last-ditch
    /// fallback because spring-voyage-style runtimes carry the same string in
    /// both their <c>provider</c> and <c>id</c> slots.
    /// </summary>
    internal static string? ResolveAgentRuntimeId(string? agent, string? provider)
    {
        if (!string.IsNullOrWhiteSpace(agent)) return agent;
        if (!string.IsNullOrWhiteSpace(provider)) return provider;
        return null;
    }

    /// <summary>
    /// Returns the post-pull probe steps the runtime does NOT declare, so
    /// the workflow can skip them. An empty array means all steps are run.
    /// </summary>
    private string[]? ComputeSkipSteps(string runtimeId, string requestedModel)
    {
        // ADR-0038: probe-step authoring lives on IAgentRuntimeLauncher.
        // The launcher to dispatch is named on the catalogue runtime entry.
        var runtime = runtimeCatalog.GetAgentRuntime(runtimeId);
        if (runtime is null)
        {
            return null;
        }

        var launcher = launcherRegistry.Get(runtime.Launcher);
        if (launcher is null)
        {
            return null;
        }

        var config = new ModelProviderInstallConfig(
            Models: Array.Empty<string>(),
            DefaultModel: requestedModel,
            BaseUrl: null);

        HashSet<ArtefactValidationStep> declared;
        try
        {
            declared = launcher.GetProbeSteps(config, string.Empty)
                .Select(s => s.Step)
                .ToHashSet();
        }
        catch
        {
            return null;
        }

        var skipped = ArtefactValidationWorkflow.PostPullSteps
            .Where(s => !declared.Contains(s))
            .Select(s => s.ToString())
            .ToArray();

        return skipped.Length > 0 ? skipped : null;
    }
}
