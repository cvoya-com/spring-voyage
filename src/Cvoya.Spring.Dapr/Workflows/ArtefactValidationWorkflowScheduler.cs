// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows;

using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Execution;

using global::Dapr.Workflow;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IArtefactValidationWorkflowScheduler"/>. Resolves the
/// unit's persisted execution defaults (<c>image</c>, <c>agent</c>,
/// <c>model</c>) and its tenant-scoped LLM credential, then schedules a
/// new <c>ArtefactValidationWorkflow</c> run via <see cref="DaprWorkflowClient"/>.
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
/// from actor-side state. Keeping this resolution out of the actor lets
/// <c>UnitActor</c> stay pure Dapr-actor code: the actor emits an intent
/// ("please schedule validation for unit id X") and the scheduler does the
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
        string unitActorId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(unitActorId))
        {
            throw new ArgumentException("Unit actor id must be supplied.", nameof(unitActorId));
        }
        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(unitActorId, out var unitActorUuid))
        {
            throw new ArgumentException(
                $"Unit actor id '{unitActorId}' is not a valid Guid.",
                nameof(unitActorId));
        }

        // Both the SpringDbContext (per-request) and the
        // ILlmCredentialResolver (scoped) live behind a fresh DI scope —
        // the scheduler itself is a singleton so it cannot consume either
        // directly through its constructor.
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var credentialResolver = scope.ServiceProvider
            .GetRequiredService<ILlmCredentialResolver>();

        // Look up the unit's user-facing name and persisted Definition
        // document by actor id. The actor keyed by Dapr actor Guid does
        // not know its name; this query is the cheapest join back to the
        // directory row that carries it. AsNoTracking: read path only.
        var entity = await db.UnitDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                u => u.Id == unitActorUuid && u.DeletedAt == null,
                cancellationToken);

        if (entity is null)
        {
            // The directory has the actor id but the canonical row is gone
            // — almost certainly a tear-down race. Surface as a structured
            // configuration failure so the actor doesn't get stuck.
            throw new ArtefactValidationSchedulingException(new ArtefactValidationError(
                Step: ArtefactValidationStep.PullingImage,
                Code: ArtefactValidationCodes.ConfigurationIncomplete,
                Message: $"No unit definition row exists for actor id '{unitActorId}'. " +
                    "The unit may have been deleted; recreate it before validating.",
                Details: null));
        }

        var defaults = DbUnitExecutionStore.Extract(entity.Definition);
        if (defaults is null)
        {
            // No execution defaults at all — closest semantic step is the
            // first one the workflow would have run (image pull). The
            // operator can fix this from the unit's Execution tab and
            // call /revalidate.
            throw new ArtefactValidationSchedulingException(new ArtefactValidationError(
                Step: ArtefactValidationStep.PullingImage,
                Code: ArtefactValidationCodes.ConfigurationIncomplete,
                Message: "No execution defaults are configured on this unit. " +
                    "Set a container image (and optionally a runtime) before validation can run.",
                Details: new Dictionary<string, string>
                {
                    ["missing"] = "image,runtime",
                }));
        }

        if (string.IsNullOrWhiteSpace(defaults.Image))
        {
            throw new ArtefactValidationSchedulingException(new ArtefactValidationError(
                Step: ArtefactValidationStep.PullingImage,
                Code: ArtefactValidationCodes.ConfigurationIncomplete,
                Message: "This unit has no container image configured. " +
                    "Set the image on the unit's Execution tab and retry validation.",
                Details: new Dictionary<string, string>
                {
                    ["missing"] = "image",
                }));
        }

        var runtimeId = ResolveAgentRuntimeId(defaults);
        if (string.IsNullOrWhiteSpace(runtimeId))
        {
            throw new ArtefactValidationSchedulingException(new ArtefactValidationError(
                Step: ArtefactValidationStep.VerifyingTool,
                Code: ArtefactValidationCodes.ConfigurationIncomplete,
                Message: "This unit has no runtime or provider configured. " +
                    "Pick a runtime (or provider) on the unit's Execution tab and retry validation.",
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
        // pass the empty string through — the workflow's RunContainerProbe
        // layer pre-filters "no credential" steps for runtimes that do not
        // authenticate.
        var providerForCredential = defaults.Provider ?? runtimeId;
        var authForCredential = AuthMethod.ApiKey;
        var catalogRuntime = runtimeCatalog.GetAgentRuntime(runtimeId);
        if (catalogRuntime is { ModelProviders.Count: > 0 })
        {
            var edge = catalogRuntime.ModelProviders[0];
            providerForCredential = edge.Id;
            authForCredential = edge.AuthMethod ?? AuthMethod.ApiKey;
        }

        var credentialResolution = await credentialResolver
            .ResolveAsync(
                providerId: providerForCredential,
                authMethod: authForCredential,
                agentId: null,
                unitId: entity.Id,
                cancellationToken);

        var credential = credentialResolution.Value ?? string.Empty;
        var requestedModel = defaults.Model ?? string.Empty;

        // Determine which post-pull steps the runtime does not declare so
        // the workflow can skip them (emitting no events → UI shows "skipped").
        // GetProbeSteps is a synchronous, non-allocating call; a minimal
        // config is sufficient — BaseUrl and models don't affect which steps
        // are declared, only the command strings within them.
        var skipSteps = ComputeSkipSteps(runtimeId, requestedModel);

        var input = new ArtefactValidationWorkflowInput(
            UnitId: unitActorId,
            UnitName: entity.DisplayName,
            Image: defaults.Image,
            RuntimeId: runtimeId,
            Credential: credential,
            RequestedModel: requestedModel,
            SkipSteps: skipSteps);

        var instanceId = await workflowClient.ScheduleNewWorkflowAsync(
            nameof(ArtefactValidationWorkflow),
            input: input);

        _logger.LogInformation(
            "Scheduled ArtefactValidationWorkflow {InstanceId} for unit {UnitName} (actor {ActorId}) image={Image} runtime={Runtime} model={Model}.",
            instanceId, entity.DisplayName, unitActorId, defaults.Image, runtimeId, requestedModel);

        return new ArtefactValidationSchedule(instanceId, entity.DisplayName);
    }

    /// <summary>
    /// Resolves the agent-runtime registry id used by
    /// <see cref="ArtefactValidationWorkflowInput.RuntimeId"/> from the unit's
    /// persisted <see cref="UnitExecutionDefaults"/> (#1683).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Precedence — <see cref="UnitExecutionDefaults.Agent"/> wins, then
    /// <see cref="UnitExecutionDefaults.Provider"/>. <c>Agent</c> is the
    /// source of truth (sourced from the manifest's <c>ai.agent</c>
    /// field by <c>UnitCreationService</c>, or set via the execution PUT endpoint).
    /// <c>Provider</c> is a last-ditch fallback because spring-voyage-style runtimes
    /// carry the same string in both their <c>provider</c> and <c>id</c> slots.
    /// </para>
    /// <para>
    /// Returns <c>null</c> when none of the slots are populated; the
    /// caller surfaces that as <c>ConfigurationIncomplete</c>.
    /// </para>
    /// </remarks>
    internal static string? ResolveAgentRuntimeId(UnitExecutionDefaults defaults)
    {
        if (!string.IsNullOrWhiteSpace(defaults.Agent)) return defaults.Agent;
        if (!string.IsNullOrWhiteSpace(defaults.Provider)) return defaults.Provider;
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
